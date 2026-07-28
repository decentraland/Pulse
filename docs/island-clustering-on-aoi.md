# Proposal: Island Clustering as a Layer on Top of Pulse AoI

Status: **draft**. Moves island authorship from Archipelago Core (see `archipelago-workers/docs/island-clustering-algorithm.md`) into Pulse, derived from the same spatial data that drives AoI.

## 1. Why

Archipelago clusters players from WS-connector heartbeats; Pulse independently decides visibility via AoI. Same positions, two pipelines, different latencies (2 s flush vs 50 ms tick) — island membership and avatar visibility can disagree (a documented archipelago issue). The current algorithm also has unstable IDs (split minorities always get new islands), a 100-peer cap that fragments connected crowds, and O(peers²) island-pair intersection tests.

Requirements for the new layer:

- No island size cap.
- Same idea — proximity-connected groups with hysteresis — not the same algorithm.
- A read-mostly layer over AoI infrastructure; never touches the per-tick hot path.
- Stable: IDs survive churn; boundary players don't flap.

## 2. What Pulse AoI provides

- **`SpatialGrid`** — global cell index, 100-unit XZ cells (`SpatialHashAreaOfInterest:CellSize`; the options class default is still 50), packed int64 keys. Written incrementally on every snapshot publish; lock-free reads.
- **`SpatialHashAreaOfInterest`** — per-observer scan of neighboring cells, realm-filtered, `MaxRadius` 100, distance tiers driving 50/100/200 ms update rates.
- **`SnapshotBoard`** — seqlock latest state per peer (position, parcel, realm); single writer per slot, lock-free reads from any thread.
- **`PeerSimulation`** — per-worker tick: AoI query → view diff → messages. Workers own disjoint peer stripes; cross-worker coordination is forbidden.

AoI is pairwise and observer-centric — no group concept exists. Islands are a global derived artifact, so they belong in one central computation over the shared boards, not in per-worker simulation.

## 3. Design

### 3.1 Components

**`IslandTracker`** — one dedicated `BackgroundService` thread. Every `PassIntervalMs` (1 s) it runs a clustering pass over thread-safe shared state and publishes to the `IslandBoard`. It writes nothing workers own.

**`IslandBoard`** — an immutable result swapped atomically (one `Volatile.Write`): `islandIdByPeer` array plus per-island metadata (id, realm, count, center, radius) and per-peer detail for stats. A separate board is justified by the repo rule: globally derived, single writer, never worker-mutated.

### 3.2 Algorithm: union-find over occupied `SpatialGrid` cells

Cluster at cell granularity, reusing the AoI grid — no second grid:

1. **Enumerate.** A small read-only `SpatialGrid` extension yields occupied cells with occupants. Occupants are partitioned by realm from their latest snapshot (realm-less peers skipped). Nodes are `(realm, cellKey)`.
2. **Union.** Weighted union-find with path compression over 8-neighbor same-realm occupied cells. Two peers in adjacent cells are between 0 and `2 · CellSize · √2` apart — **0–283 units** at the configured 100 u — a proxy for archipelago's 64-unit join distance that is 4.4× coarser; boundary noise is absorbed by the dwell debounce (§3.3). Effective join range follows the AoI `CellSize`: one spatial resolution for visibility and clustering.

   Two consequences of the 100 u setting worth stating plainly. Pulse will merge groups archipelago would have kept apart far more often than a 50 u grid would, so the §5 shadow-mode comparison should expect systematically fewer, larger clusters rather than a near-match. And since the join band (283 u) now exceeds `MaxRadius` (200 u) by a wider margin, two peers being in one cluster while never being mutually visible is ordinary rather than a chaining edge case — see the "Island ≠ visibility" note below.
3. **Component → island.** Each root is an island; count, centroid, radius computed in the same sweep.

No cap: a connected crowd is one island, deterministically.

**Limits.** Hard ceiling: instance capacity (`Transport.MaxPeers`, 4095 — fixed arrays, no cross-instance clustering), further confined per realm. The algorithm adds none: O(N + C), C = occupied cells. Measured **~590 µs/pass at the 4095 ceiling** — Genesis City-sized world, cold working set, `ClusterTrackerBenchmarks` scenario `CeilingUniform` (~424 µs warm; ~230 KB of gen0 per pass, almost all of it the immutable `ClusterPass` the readers hold). The documented scenarios cost far less: ~9 µs for the 100-peer sparse case, ~50–57 µs for the 1 000-peer ones. That is 0.06% of one core at the 1 Hz cadence, so the pass is not a scaling concern below a capacity change; an earlier estimate of "well under 1 ms at 10k peers" was optimistic, since 10k is not reachable on one instance anyway. The practical bound is density and belongs to AoI: islands don't affect fan-out, so spread islands can reach instance capacity while dense crowds hit per-observer AoI fan-out (~n² within `MaxRadius`) first. Downstream, LiveKit room mapping shards past ~low thousands (§3.6).

**Percolation limit — at capacity on a full-size realm, the partition collapses.** Cell-adjacency clustering is site percolation on the grid: once the occupied fraction passes the 8-neighbour (Moore) threshold of ≈ 0.407, the occupied cells form one giant connected component and every peer lands in one cluster. Genesis City (4800 u) at 100 u cells is 48 × 48 = 2304 cells, so `MaxPeers` 4095 spread uniformly gives λ ≈ 1.78 peers/cell and an occupied fraction of `1 − e^−λ` ≈ **0.83** — roughly twice the threshold. Measured (`ClusterTrackerBenchmarks`, `CeilingUniform`): 1904 occupied cells, **2 clusters, the larger holding 4091 of 4095 peers**.

At 50 u the same population occupies ≈ 0.36 of 9216 cells, just *below* threshold — which is why the design's worked examples partitioned sensibly. Doubling the cell size moved the shipping configuration from one side of the percolation transition to the other. Consequences: sticky IDs and the dwell debounce have nothing to stabilise at high density; downstream LiveKit room sharding is load-bearing rather than an overflow path (§3.6); and the exact-distance refinement in §7 becomes the mechanism that decides whether clustering means anything at capacity. This bounds usefulness, not correctness — the pass still runs in well under a millisecond, and sparse realms (the common case, scenario 3) are unaffected.

#### Worked examples

> These three scenarios and their illustrations were produced at a **50-unit** cell size, before the configured value settled at 100. At 100 u the same inputs merge more aggressively, so every cluster count below is a lower bound on merging and an upper bound on cluster count. The qualitative comparisons against archipelago — cap fragmentation, chaining, the sparse-case match — all still hold; the specific counts do not. Regenerating the illustrations is tracked in §7.
>
> All three are reproduced as runnable benchmarks — `ClusterScenario` in `src/DCLPulseBenchmarks`, which prints the realized topology at setup. At 100 u they still land on the documented partitions: scenario 1 gives 9 clusters with the crowd intact at 450, scenario 2 one cluster of 1 000, scenario 3 the 32-peer bridge plus singleton loners. What does *not* survive the cell-size change is a densely populated realm — see the percolation note above.

**Scenario 1 — one dense region (450) + 10 sparser regions, 1 000 peers:**

![Scenario 1: dense + sparse regions](img/island-clustering-illustration.png)

Panels: input → grid occupancy with union-find edges → Pulse result → today's archipelago. Pulse: 9 islands; the dense crowd is one island of 450; close (I2, 160) and chained (I3, 80) regions merge. Archipelago: 14 islands; the cap slices the dense crowd into 5 overlapping rooms whose boundaries cut through the crowd.

**Scenario 2 — chaining worst case. 10 areas of 100 in a line, each bridging into the next (~1.2 km):**

![Scenario 2: chained areas](img/island-clustering-chain-illustration.png)

Adjacency is transitive: one island of 1 000, extent ~1 280 units. Notes:

- **Island ≠ visibility.** The dashed circle is one observer's AoI (~2 of 10 areas). Chain ends share an island but never exchange state. Consumers must not read "same island" as "near each other".
- **Chaining is inherited, not new** — archipelago links the same chain into one group, then the cap slices it into 10 rooms with merge-order-dependent membership. Pulse reports the chain honestly and stably.
- **If bounded extent is ever needed:** a max-diameter constraint in the tracker (split at the sparsest bridging cell). Not in iteration 1; extent is already reported (center, radius), so consumers can detect chains.

**Scenario 3 — sparse steady state. 100 peers: small areas (one pair connects, one doesn't), a duo, many loners:**

![Scenario 3: sporadic distribution](img/island-clustering-sporadic-illustration.png)

The common case, and the migration is invisible in it: sparse cells have no occupied neighbors (near-zero union edges); adjacent areas bridge (I1, 32); the near-but-separate pair stays split; each loner is a singleton island — archipelago's semantic exactly. On this input both algorithms produce the **identical partition** (672/672 co-membership pairs). Divergence exists only in scenarios 1–2: cap fragmentation and the boundary band (50–141 u as illustrated; 100–283 u at the configured cell size, which widens it considerably).

### 3.3 Stability

**Sticky IDs.** Each pass, a component inherits the ID of the previous island sharing the most members (ties → older). Unmatched components get fresh IDs. IDs survive churn; on split, continuity (not size) decides who keeps the ID.

**Dwell debounce.** Temporal hysteresis instead of archipelago's join/leave distances: a published assignment changes only after `DwellPasses` (3) consecutive passes agree. Immediate exceptions: first assignment, teleport, realm change, island deletion. State: one `(candidateId, streak)` pair per peer.

Stability is load-bearing: ID churn breaks dashboard continuity and forces LiveKit room rejoins downstream.

### 3.4 Consumption: server-side only

No client protocol change — no `IslandChanged` message, no worker involvement. Two consumers:

- **Stats/monitoring** — served by Pulse directly (§3.5).
- **Comms Gatekeeper as listener** — Pulse is the sole author and publishes assignment changes to NATS (§3.6); gatekeeper mints LiveKit conn-strings and re-emits the existing `island_changed` message, so WS Connector and clients are untouched. Archipelago-core is removed, not bridged.

### 3.5 Stats served by Pulse

"Stats" here means entity-level JSON (island rosters, wallet ↔ position ↔ parcel records) consumed by product surfaces — distinct from Prometheus `/metrics`, which stays the aggregate time-series endpoint (island count, pass duration) for dashboards and alerting.

Today: WS Connector → NATS heartbeats + core → `engine.islands` → stats service REST (`/peers`, `/islands`, `/parcels`, `/hot-scenes`; per peer: wallet, position, parcel, lastPing). Pulse already holds all of it — wallet (`IdentityBoard`), position/parcel/realm/freshness (`SnapshotBoard`), assignment (`IslandBoard`) — and the tracker attaches per-peer detail to each published result, so HTTP serves the last pass at zero marginal cost, ≤ `PassIntervalMs` stale (vs 2 s flush + 60 s heartbeat timeout today).

Surface: `GET /islands`, `/islands/{id}`, `/peers`, `/parcels` on the existing `HttpService`, response-compatible with archipelago stats (`maxPeers` constant or omitted). These are stats endpoints only — the assignment feed is NATS (§3.6). Exception: `/hot-scenes` needs Catalyst metadata — it moves to comms-gatekeeper, which already has a Catalyst `content-client`; Pulse grows no Catalyst client.

### 3.6 Feed: Pulse → NATS (direct)

Pulse publishes per-peer assignment changes **directly to NATS**: `peer.{addr}.island_change { islandId, realm }`, emitted whenever a peer's *published* (post-debounce) assignment changes. Comms-gatekeeper subscribes, mints the LiveKit conn-string (ban check is authority-local — its own store), and publishes the existing `engine.peer.{addr}.island_changed` `IslandChangedMessage`. WS Connector and clients are untouched; archipelago-core is removed rather than bridged. (An earlier revision proposed core polling `GET /islands` over HTTP; dropped in favor of direct NATS once core's removal moved into iteration 1.)

Why NATS works here where the topology-snapshot feed ruled it out: per-peer *change events* are small and self-contained — the 1 MB payload concern applied to full-topology snapshots, which this feed doesn't carry. Both consumers in the chain (gatekeeper, WS Connector) are NATS-side services, and event semantics match the consumer: gatekeeper acts per change, not per snapshot. The cost is Pulse's first broker dependency — **publish-only, config-gated, fail-soft**: a NATS outage stalls conn-string delivery, never the tracker or simulation.

Delivery semantics: NATS is at-most-once — a dropped event leaves a peer in its previous room until its next reassignment. Mitigation: a low-rate periodic re-publish of current assignments (sweep), plus `GET /islands` (stats) available for reconciliation.

**Connecting Pulse to NATS** (implementation): NATS.Net official client (pure managed — no native deps; the `<PackageReference>` still triggers the Docker-image rebuild rule). One `NatsPublisher : BackgroundService` owns the connection; producers write to a bounded channel (drop-oldest — feeds are self-superseding, a stalled broker must never back-pressure the tracker): `IslandTracker` emits `island_change` + `engine.islands` + parcel-change batches (it already keeps per-peer previous state for the dwell debounce, so parcel diffing is free), a timer emits `engine.discovery`. Wire compatibility: `engine.islands`/`engine.discovery` must match `@dcl/protocol` `archipelago.proto` messages — extend Pulse's existing proto generation to include them; the two new messages (`island_change`, `parcel_changes`) are added to `@dcl/protocol` so gatekeeper gets TS types from the same source. Test seam: `IIslandFeedPublisher` (NSubstitute). `Nats` config unset = feed disabled, stats-only mode — the rollback story.

**Consequences owned by gatekeeper:**

- *Uncapped islands vs room capacity* — LiveKit rooms are single-node-bound (~low thousands audio; 100 audio subscriptions per participant). Past that, gatekeeper shards rooms (`island:{id}:{shard}`) — a token-issuer concern, decoupled from clustering.
- *Delivery reach* — a wallet connected to Pulse but without a WS Connector session gets events nobody forwards (harmless); the reverse (WS session, no Pulse connection) gets no island. Expose a mismatch metric.

### 3.7 Configuration

| Option (`Islands`) | Default | Meaning |
| --- | --- | --- |
| `Enabled` | false | Feature flag |
| `PassIntervalMs` | 1000 | Tracker pass cadence |
| `DwellPasses` | 3 | Passes before a reassignment publishes |
| `IdPrefix` | `I` | Island ID prefix (replaces archipelago `ROOM_PREFIX`) |
| `Nats` (url, subjects) | — | Feed publisher; unset = feed disabled (stats-only mode) |

## 4. Comparison

| | Archipelago Core | This proposal |
| --- | --- | --- |
| Input | NATS heartbeats (≤ 2 s stale, 60 s disconnect lag) | `SnapshotBoard` (fresh, ~5 s cleanup) |
| Granularity | peer-pairwise single-linkage, 64/80 | union-find over 100 u `SpatialGrid` cells (join band 0–283 u) |
| Stability | ID survives largest fragment only; merge-order dependent | sticky IDs + dwell debounce |
| Size cap | 100, blocks merges | none; sharding downstream |
| Cost | O(pairs) per flush | O(N + C) per pass, off hot path |
| Consistency with visibility | none | same boards as AoI |

## 5. Migration

Plan of record: [Archipelago ⇒ Pulse migration plan](https://app.notion.com/p/decentraland/Archipelago-Pulse-migration-plan-3a45f41146a58070b6b0dbe541bc7533) (Notion). This section mirrors it.

**Iteration 1 — remove archipelago-core.** Pulse becomes the sole island author; comms-gatekeeper takes over conn-string minting; WS Connector keeps its client-facing role unchanged.

1. Shadow mode: ship `IslandTracker`/`IslandBoard` behind `Islands.Enabled` + Prometheus metrics (island count, pass duration, reassignments/min). Compare topologies against archipelago offline.
2. Switch: Pulse publishes `peer.{addr}.island_change` to NATS (§3.6); gatekeeper (new NATS client — it has none today) subscribes, mints, publishes the existing `engine.peer.{addr}.island_changed`; WS Connector forwards as before. Core is decommissioned (kept deployable behind a flag as rollback until shadow comparison passes).
3. **Remove `desiredRoom`.** It dies with core's engine — it only ever biased merge-target choice when the 100 cap split co-located crowds, which no longer happens. Validated: no production client sets it (unity-explorer's `SendHeartbeatAsync` sends only `Position`; godot-explorer doesn't send it; the sole sender is the test client). The proto field stays, silently ignored; the test client drops its usage.
4. **Client heartbeats stay** (decision — removal deferred to iteration 2). Archipelago-stats keeps building its peer map from `peer.*.heartbeat`, so every stats endpoint is untouched in iteration 1. Pulse-published `engine.islands` + `engine.discovery` replace core's feeds. Iteration-1 Pulse NATS output: `peer.{addr}.island_change`, `engine.islands`, `engine.discovery`.

**Iteration 1 client-side complement — crowd ghost avatars (unity-explorer).** Uncapped islands remove the last server-side bound on co-located crowd size, so the client's GPU becomes the binding constraint; excess visible peers render as ghosts instead of full avatars. The pieces mostly exist:

- *Already there:* ghost rendering (`AvatarBase.GhostGameObject` + `GhostHologram` shader, one renderer + per-avatar material tinted by profile color) — today used only as a loading transition by `AvatarGhostSystem` (reveal → wearable handoff → hidden); budget plumbing (`IPerformanceBudget` frame-time + memory budgets already gate `AvatarInstantiatorSystem`); quality tiers (`QualitySettingsAsset` / `IQualityLevelController`); per-subject distance tiers from Pulse AoI deltas.
- *New:* a ghost **steady state** — an avatar deliberately held at ghost stage, skipping wearable download/instantiation/skinning entirely (that's where the GPU/memory cost lives), keeping base-body ghost + movement + nameplate. A new `AvatarCrowdBudgetSystem` (before `AvatarInstantiatorSystem` in `AvatarGroup`) ranks visible avatar entities by camera distance/AoI tier each throttled frame and grants full-avatar status to the top K. K is **GPU-driven**: the quality tier anchors `[Kmin, Kmax]` (e.g. Low 20 / Mid 50 / High 100), and a slow AIMD controller adjusts within it against measured GPU frame time — already available via `Profiler.LastGpuFrameTimeValueNs` (`ProfilerRecorder` "GPU Frame Time") with `PerformanceBottleneckDetector` (`FrameTimingManager`) gating demotions to GPU-bound frames only, since the signal is global (scene + avatars). Controller cadence ~1 Hz with EMA, dead band, and cooldown ≥ transition duration (GPU timings lag a few frames; demotions free cost late); falls back to CPU frame time + fixed tier caps where the GPU recorder is unsupported. Grant changes additionally need rank hysteresis (margin + dwell — same reasoning as the island debounce) so boundary avatars don't flap.
- *Transitions:* promotion reuses the existing ghost→full reveal unchanged; demotion is new — reverse reveal, then release wearables through the existing cleanup/pool path. Emote props/audio suppressed while ghosted.
- *Risks:* demotion churn pressuring wearable pools (mitigated by hysteresis); ghosts are skinned meshes, cheap but not free — past a second threshold, hide outright.

**Iteration 2 — remove archipelago-stats + client heartbeats.** Client heartbeats retire (position already flows via `MovementInput`; WS Connector heartbeat intake and `peer.*.heartbeat`/`peer.*.disconnect` go with them). Endpoints migrate to Pulse (`/peers`, `/peers/:id`, `/islands`, `/islands/:id`, `/parcels`, `/status`; `/comms/*` aliases kept; `/core-status` retires — Pulse `/about` + `/health` carry commit + user count), except `/hot-scenes`, which moves to **comms-gatekeeper**. Its position source (heartbeats are gone; the island feed carries assignments, not positions) is NATS — preferred over HTTP between internal services: Pulse publishes `engine.parcel_changes` every N s (default 2 s), a batch of peers whose parcel changed since the last batch (`{ seq, changes: [{ address, realm, parcel | null on disconnect }] }`) — movers only, so batches stay small (worst case `MaxPeers` entries). Gatekeeper maintains wallet → parcel from the deltas, derives per-parcel counts, and joins with cached Catalyst scene metadata via its existing `content-client` — Pulse grows none (§3.5). Drift-healing: a `seq` gap or a periodic timer triggers a full snapshot on the same subject. Public URLs stay stable by rerouting at the CloudFlare edge to the new origins, so consumers need response-shape compatibility only. Impact per consumer:

| Service | Uses today | Iteration 2 change |
| --- | --- | --- |
| `realm-provider` | Stats `/core-status`, WS Connector `/status`; builds `archipelago:wss://…/ws` adapter URL | `/core-status` → Pulse health/about; `/status` + adapter URL unchanged (tied to WS Connector, not stats) |
| `lamb2` | WS Connector `/status` for health; `/archipelago/ws` adapter URL in Catalyst `/about` | None — depends on WS Connector lifetime, not stats |
| `social-service-ea` | Stats `/peers` (connected peer list) | → Pulse `GET /peers` (edge reroute, no change on their side) |
| `unity-explorer` | Stats `/comms/peers`, `/status`, `/hot-scenes` | `/comms/peers` → Pulse; `/status` → Pulse; `/hot-scenes` → gatekeeper (URLs stay via edge reroute) |
| `godot-explorer` | Stats `/hot-scenes`, `/status` | `/status` → Pulse; `/hot-scenes` → gatekeeper |
| `referral` | Stats `/peers` (is user in main realm) | → Pulse `GET /peers` (realm now first-class in the response) |
| `places` | Stats `/hot-scenes` via realm-provider proxy | None — edge reroute keeps the URL |
| `sites` | Stats `/hot-scenes` | None — edge reroute keeps the URL |
| `dcl-comms-debugger` | WS Connector `/ws` directly (load testing) | None — transport, not stats |

Takeaways: `/hot-scenes` is the widest-used endpoint (4 consumers) and never moves into Pulse — it lands on gatekeeper's existing Catalyst infrastructure. `/peers` consumers (3) are the real re-pointing work; Pulse responses stay shape-compatible (including the legacy `/comms` prefix) and edge rerouting keeps URLs stable. WS Connector-coupled consumers (`realm-provider` adapter URL, `lamb2`, `dcl-comms-debugger`) are untouched until the WS Connector retirement (§6).

No client protocol change in iterations 1–2; rollback is config-only.

## 6. Future migrations (proposals, out of scope)

Not part of iterations 1–2; recorded as direction.

| Proposal | Before | After | Implementation glimpse |
| --- | --- | --- | --- |
| **WS Connector retirement** | Clients hold a WS session whose only unique function is delivering the LiveKit conn-string; auth and ban kicks duplicate Pulse | No WS Connector, no island NATS subjects; clients get island ID from Pulse and exchange it for a token | Minimal `IslandChanged { island_id, realm }` on ch0 (the per-worker announce path omitted from iteration 1); token exchange at comms-gatekeeper (it already mints and owns sharding after iteration 1) — push becomes pull, gatekeeper's NATS subscription retires. Preconditions: clients on a Pulse transport (WebTransport for browsers), `realm-provider`/`lamb2` advertise the Pulse endpoint |
| **Ban enforcement consolidation** | After iteration 1, three enforcement points remain: Pulse (poll + handshake reject + mid-session kick), WS Connector (per-handshake check + 30 s sweep), client status screen (core's token-mint check already became gatekeeper-local in iteration 1) | Pulse is the single comms-level enforcement point + gatekeeper checks its own store at minting; client status screen unchanged | WS Connector's two checks vanish with its retirement; Pulse's existing `BansPollingHttpService`/`BanEnforcer` needs no change. Ban **origination** stays in comms-gatekeeper throughout (its internals not analyzed — repo out of scope) |
| **WS Connector `/status` + realm advertisement** | `realm-provider`/`lamb2` poll WS Connector `/status` and advertise `archipelago:wss://…/ws`; one archipelago deployment = one realm | Pulse public status endpoint; adapter string names the Pulse endpoint; one Pulse instance hosts many realms | Status is a subset of Pulse's stats surface. Advertisement change is a `realm-provider`/`lamb2` config/format change. New concern to plan: per-instance capacity (`MaxPeers` 4095) now bounds the *sum* of hosted realms — multi-instance sharding is an open question |

Everything else archipelago does is already Pulse-native and needs no migration: AuthChain auth, duplicate-session eviction, heartbeat expiry (Pulse: ~5 s disconnect cleanup vs 60 s), and kick messaging (`DisconnectReason`).

## 7. Open questions

- Scene listeners in islands? Proposal: no — no snapshot, naturally excluded.
- Exact-distance refinement if cell adjacency proves too coarse. At the configured 100 u the join band is 0–283 u against archipelago's 64 u, so this is now more likely to be needed than when the proposal was written against 50 u cells — thresholds scale with `CellSize` (merge at ~`2·CellSize·√2`, split at just above `CellSize`). Deferred until shadow-mode data.
- Regenerate the §3.2 worked examples and illustrations at 100 u, so the documented cluster counts match the shipping configuration.
- Should `SpatialHashAreaOfInterestOptions.CellSize` default to 100 to match `appsettings.json`? A default that differs from every deployment is a trap for tests and for anyone reading the class.
- Stats endpoints public (as archipelago's are) or bearer-gated like `/metrics`? They expose wallet ↔ position; decide deliberately.
