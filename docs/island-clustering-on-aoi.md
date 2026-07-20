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

- **`SpatialGrid`** — global cell index, 50-unit XZ cells, packed int64 keys. Written incrementally on every snapshot publish; lock-free reads.
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
2. **Union.** Weighted union-find with path compression over 8-neighbor same-realm occupied cells. Adjacent-cell peers are 0–141 units apart — a coarser proxy for archipelago's 64-unit join distance; boundary noise is absorbed by the dwell debounce (§3.3). Effective join range follows the AoI `CellSize`: one spatial resolution for visibility and clustering.
3. **Component → island.** Each root is an island; count, centroid, radius computed in the same sweep.

No cap: a connected crowd is one island, deterministically.

**Limits.** Hard ceiling: instance capacity (`Transport.MaxPeers`, 4095 — fixed arrays, no cross-instance clustering), further confined per realm. The algorithm adds none (O(N + C), C = occupied cells; well under 1 ms/pass at 10k peers). The practical bound is density and belongs to AoI: islands don't affect fan-out, so spread islands can reach instance capacity while dense crowds hit per-observer AoI fan-out (~n² within `MaxRadius`) first. Downstream, LiveKit room mapping shards past ~low thousands (§3.6).

#### Worked examples

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

The common case, and the migration is invisible in it: sparse cells have no occupied neighbors (near-zero union edges); adjacent areas bridge (I1, 32); the near-but-separate pair stays split; each loner is a singleton island — archipelago's semantic exactly. On this input both algorithms produce the **identical partition** (672/672 co-membership pairs). Divergence exists only in scenarios 1–2: cap fragmentation and the 50–141 u boundary band.

### 3.3 Stability

**Sticky IDs.** Each pass, a component inherits the ID of the previous island sharing the most members (ties → older). Unmatched components get fresh IDs. IDs survive churn; on split, continuity (not size) decides who keeps the ID.

**Dwell debounce.** Temporal hysteresis instead of archipelago's join/leave distances: a published assignment changes only after `DwellPasses` (3) consecutive passes agree. Immediate exceptions: first assignment, teleport, realm change, island deletion. State: one `(candidateId, streak)` pair per peer.

Stability is load-bearing: ID churn breaks dashboard continuity and forces LiveKit room rejoins downstream.

### 3.4 Consumption: server-side only

No client protocol change — no `IslandChanged` message, no worker involvement. Two consumers:

- **Stats/monitoring** — served by Pulse directly (§3.5).
- **Archipelago as listener** — Pulse is the sole author; archipelago core stops clustering, consumes the feed (§3.6), and keeps its external interface (conn-strings, NATS subjects, endpoints) unchanged.

### 3.5 Stats served by Pulse

"Stats" here means entity-level JSON (island rosters, wallet ↔ position ↔ parcel records) consumed by product surfaces — distinct from Prometheus `/metrics`, which stays the aggregate time-series endpoint (island count, pass duration) for dashboards and alerting.

Today: WS Connector → NATS heartbeats + core → `engine.islands` → stats service REST (`/peers`, `/islands`, `/parcels`, `/hot-scenes`; per peer: wallet, position, parcel, lastPing). Pulse already holds all of it — wallet (`IdentityBoard`), position/parcel/realm/freshness (`SnapshotBoard`), assignment (`IslandBoard`) — and the tracker attaches per-peer detail to each published result, so HTTP serves the last pass at zero marginal cost, ≤ `PassIntervalMs` stale (vs 2 s flush + 60 s heartbeat timeout today).

Surface: `GET /islands`, `/islands/{id}`, `/peers`, `/parcels` on the existing `HttpService`, response-compatible with archipelago stats (`maxPeers` constant or omitted). Exception: `/hot-scenes` needs Catalyst metadata — keep a thin aggregator for it, re-pointed at Pulse; Pulse grows no Catalyst client.

### 3.6 Feed: Pulse → Archipelago

Core **polls `GET /islands`** at its ~2 s cadence, diffs assignments per wallet, mints LiveKit conn-strings, republishes `engine.peer.{id}.island_changed`. Downstream sees no change.

**Why HTTP polling** (vs NATS/WS/ENet): the feed is a periodic self-superseding snapshot — pull fits exactly, a missed poll heals itself. Both sides have the machinery already. NATS adds Pulse's only external dependency and its 1 MB payload cap forces chunking at ~10k peers. WS needs a new server stack (`HttpListener` has no Linux WS upgrade) for latency the 2 s consumer can't use. ENet is a game-client transport with no Node bindings. Failure mode is visible staleness, not a dead subscription. Revisit NATS only with multiple consumers.

**ETag.** `"{bootId}-{version}"`, string-compared against `If-None-Match` before serialization. The feed returns an assignments-only shape (id, realm, member wallets — no positions); its version bumps only when assignments change, which sticky IDs + dwell make infrequent → steady-state polls are empty 304s. The boot ID (a GUID generated at process start) invalidates held ETags across restarts — without it, the reset in-memory version counter could reach a previously served value and 304 a topology the consumer never saw. Version and payload live on the same immutable object — no torn reads. Position-carrying stats endpoints are served unconditioned.

**Latency upgrade path**, if ever needed: long-polling (`?after={version}&timeout=30`) — push-grade latency, request/response semantics, inherent failure detection. SSE/WS is explicitly not the path.

**Bridge-owned consequences:**

- *Population mismatch* — peer sets joined by wallet can transiently differ; unknown wallets get no island until they appear (same lag as today). Expose a mismatch metric.
- *Uncapped islands vs room capacity* — LiveKit rooms are single-node-bound (~low thousands audio; 100 audio subscriptions per participant). Past that the bridge shards rooms (`island:{id}:{shard}`) — a token-issuer concern, decoupled from clustering.

### 3.7 Configuration

| Option (`Islands`) | Default | Meaning |
| --- | --- | --- |
| `Enabled` | false | Feature flag |
| `PassIntervalMs` | 1000 | Tracker pass cadence |
| `DwellPasses` | 3 | Passes before a reassignment publishes |

## 4. Comparison

| | Archipelago Core | This proposal |
| --- | --- | --- |
| Input | NATS heartbeats (≤ 2 s stale, 60 s disconnect lag) | `SnapshotBoard` (fresh, ~5 s cleanup) |
| Granularity | peer-pairwise single-linkage, 64/80 | union-find over 50 u `SpatialGrid` cells |
| Stability | ID survives largest fragment only; merge-order dependent | sticky IDs + dwell debounce |
| Size cap | 100, blocks merges | none; sharding downstream |
| Cost | O(pairs) per flush | O(N + C) per pass, off hot path |
| Consistency with visibility | none | same boards as AoI |

## 5. Migration

**Iteration 1 — data moves, endpoints don't.** Pulse becomes the sole island author and the source of all island/peer data. Every external endpoint stays exactly where it is: the archipelago stats service keeps serving its REST API (now sourced from Pulse's feed via core), WS Connector keeps delivering `island_changed`. No consumer changes anything.

1. Shadow mode: ship behind `Islands.Enabled`; expose `GET /islands` + Prometheus metrics (island count, pass duration, reassignments/min). Compare topologies against archipelago offline.
2. Switch authorship: core's engine → feed subscriber (core-side flag); core only diffs, mints conn-strings, republishes `island_changed` and `engine.islands` (keeping the stats service fed unmodified).
3. **Remove `desiredRoom`.** The hint dies with the engine — it only ever biased merge-target choice when the 100 cap split co-located crowds, which no longer happens. Validated: no production client sets it — unity-explorer's `SendHeartbeatAsync` sends only `Position`, godot-explorer doesn't send it either; the sole sender is the test client. Scope: core stops mapping it to `preferedIslandId`; the field stays in the `Heartbeat` proto (silently ignored) for wire compatibility; the test client drops its usage.

**Iteration 1 client-side complement — crowd ghost avatars (unity-explorer).** Uncapped islands remove the last server-side bound on co-located crowd size, so the client's GPU becomes the binding constraint; excess visible peers render as ghosts instead of full avatars. The pieces mostly exist:

- *Already there:* ghost rendering (`AvatarBase.GhostGameObject` + `GhostHologram` shader, one renderer + per-avatar material tinted by profile color) — today used only as a loading transition by `AvatarGhostSystem` (reveal → wearable handoff → hidden); budget plumbing (`IPerformanceBudget` frame-time + memory budgets already gate `AvatarInstantiatorSystem`); quality tiers (`QualitySettingsAsset` / `IQualityLevelController`); per-subject distance tiers from Pulse AoI deltas.
- *New:* a ghost **steady state** — an avatar deliberately held at ghost stage, skipping wearable download/instantiation/skinning entirely (that's where the GPU/memory cost lives), keeping base-body ghost + movement + nameplate. A new `AvatarCrowdBudgetSystem` (before `AvatarInstantiatorSystem` in `AvatarGroup`) ranks visible avatar entities by camera distance/AoI tier each throttled frame and grants full-avatar status to the top K. K is **GPU-driven**: the quality tier anchors `[Kmin, Kmax]` (e.g. Low 20 / Mid 50 / High 100), and a slow AIMD controller adjusts within it against measured GPU frame time — already available via `Profiler.LastGpuFrameTimeValueNs` (`ProfilerRecorder` "GPU Frame Time") with `PerformanceBottleneckDetector` (`FrameTimingManager`) gating demotions to GPU-bound frames only, since the signal is global (scene + avatars). Controller cadence ~1 Hz with EMA, dead band, and cooldown ≥ transition duration (GPU timings lag a few frames; demotions free cost late); falls back to CPU frame time + fixed tier caps where the GPU recorder is unsupported. Grant changes additionally need rank hysteresis (margin + dwell — same reasoning as the island debounce) so boundary avatars don't flap.
- *Transitions:* promotion reuses the existing ghost→full reveal unchanged; demotion is new — reverse reveal, then release wearables through the existing cleanup/pool path. Emote props/audio suppressed while ghosted.
- *Risks:* demotion churn pressuring wearable pools (mitigated by hysteresis); ghosts are skinned meshes, cheap but not free — past a second threshold, hide outright.

**Iteration 2 — consumers re-point to Pulse directly,** except `/hot-scenes`. That endpoint joins peer-per-parcel counts with scene metadata fetched from Catalyst; the count side moves to Pulse, but the Catalyst side stays in a slimmed-down stats service (the "aggregator") whose sole remaining job is that join — Pulse grows no Catalyst client (§3.5). Impact per consumer:

| Service | Uses today | Iteration 2 change |
| --- | --- | --- |
| `realm-provider` | Stats `/core-status`, WS Connector `/status`; builds `archipelago:wss://…/ws` adapter URL | `/core-status` → Pulse health/about; `/status` + adapter URL unchanged (tied to WS Connector, not stats) |
| `lamb2` | WS Connector `/status` for health; `/archipelago/ws` adapter URL in Catalyst `/about` | None — depends on WS Connector lifetime, not stats |
| `social-service-ea` | Stats `/peers` (connected peer list) | → Pulse `GET /peers` |
| `unity-explorer` | Stats `/comms/peers`, `/status`, `/hot-scenes` | `/comms/peers` → Pulse `GET /peers`; `/status` → Pulse; `/hot-scenes` → aggregator (URL may stay) |
| `godot-explorer` | Stats `/hot-scenes`, `/status` | `/status` → Pulse; `/hot-scenes` → aggregator |
| `referral` | Stats `/peers` (is user in main realm) | → Pulse `GET /peers` (realm now first-class in the response) |
| `places` | Stats `/hot-scenes` via realm-provider proxy | None — aggregator keeps the endpoint; proxy re-points if the aggregator URL changes |
| `sites` | Stats `/hot-scenes` | None — aggregator |
| `dcl-comms-debugger` | WS Connector `/ws` directly (load testing) | None — transport, not stats |

Takeaways: `/hot-scenes` is the widest-used endpoint (4 consumers) and never moves into Pulse — the aggregator must survive iteration 2. `/peers` consumers (3) are the real re-pointing work; Pulse responses stay shape-compatible (including the legacy `/comms` prefix) to keep those changes URL-only. WS Connector-coupled consumers (`realm-provider` adapter URL, `lamb2`, `dcl-comms-debugger`) are untouched until the WS Connector retirement (§6).

No client protocol change in iterations 1–2; rollback is config-only.

## 6. Future migrations (proposals, out of scope)

Not part of iterations 1–2; recorded as direction.

| Proposal | Before | After | Implementation glimpse |
| --- | --- | --- | --- |
| **WS Connector retirement** | Clients hold a WS session whose only unique function is pushing the LiveKit conn-string; auth, positions (sent twice — WS heartbeat + `MovementInput`), and ban kicks all duplicate Pulse | No WS Connector, no core bridge, no NATS subjects, single position stream; clients get island ID from Pulse and exchange it for a token | Minimal `IslandChanged { island_id, realm }` on ch0 (the per-worker announce path omitted from iteration 1); token exchange at comms-gatekeeper, which validates against Pulse's feed and owns room sharding. Preconditions: clients on a Pulse transport (WebTransport for browsers), `realm-provider`/`lamb2` advertise the Pulse endpoint |
| **Hot-scenes into Pulse** | Thin aggregator kept alive for one endpoint; hits Catalyst uncached on every request | Aggregator retires; Pulse serves `/hot-scenes` with bounded staleness | `IslandBoard` pattern: background refresher joins parcel counts (already in Pulse) with a minutes-TTL scene-metadata cache (`fetchEntitiesByPointers` on uncached tiles only); endpoint serves the immutable last snapshot — never request-path I/O (the `HttpService` loop is sequential). Catalyst client is Pulse's first external dependency: isolated `BackgroundService`, fail-soft, staleness metric. Fallback if rejected: move the endpoint to `places` |
| **Ban enforcement consolidation** | Four parallel fail-open enforcement implementations against comms-gatekeeper: Pulse (poll + handshake reject + mid-session kick), WS Connector (per-handshake check + 30 s sweep), core (check at token mint), client status screen — two marked "keep in sync" duplicates | Pulse is the single comms-level enforcement point + gatekeeper checks its own store at token exchange; client status screen unchanged | WS Connector's two checks vanish with the service; core's token-mint check moves into gatekeeper (authority-local, no cross-service hop). Pulse's existing `BansPollingHttpService`/`BanEnforcer` needs no change. Ban **origination** stays in comms-gatekeeper throughout (its internals not analyzed — repo out of scope) |
| **LiveKit token minting** | Core mints room-join JWTs itself (5-min TTL; grants: mic-only publish, data, subscribe) | Gatekeeper mints at token exchange | Grant policy and room-sharding rules port from `core/components.ts` `mintToken` to gatekeeper; Pulse never holds LiveKit credentials |
| **Service discovery / health** | Core publishes `engine.discovery` (name, commit, user count) every 10 s → stats `/core-status` (healthy = heartbeat < 90 s old) → `realm-provider` | Pulse `/about` + `/health` serve the same signal directly | Add user count to Pulse's `/about` (commit hash already there); `realm-provider` polls it instead of `/core-status`. The NATS discovery subject retires |
| **Island ID convention (`ROOM_PREFIX`)** | Sequential IDs `{prefix}{n}` per deployment, prefix from config | Same convention on Pulse sticky IDs | One `Islands.IdPrefix` option stamped by the tracker when assigning fresh IDs — keeps room names unambiguous per deployment |
| **WS Connector `/status` + realm advertisement** | `realm-provider`/`lamb2` poll WS Connector `/status` and advertise `archipelago:wss://…/ws`; one archipelago deployment = one realm | Pulse public status endpoint; adapter string names the Pulse endpoint; one Pulse instance hosts many realms | Status is a subset of Pulse's stats surface. Advertisement change is a `realm-provider`/`lamb2` config/format change. New concern to plan: per-instance capacity (`MaxPeers` 4095) now bounds the *sum* of hosted realms — multi-instance sharding is an open question |

Everything else archipelago does is already Pulse-native and needs no migration: AuthChain auth, duplicate-session eviction, heartbeat expiry (Pulse: ~5 s disconnect cleanup vs 60 s), and kick messaging (`DisconnectReason`).

## 7. Open questions

- Scene listeners in islands? Proposal: no — no snapshot, naturally excluded.
- Exact-distance refinement if cell adjacency proves too coarse (merge at ~140 u diagonal / split at ~60 u across boundaries). Deferred until shadow-mode data.
- Stats endpoints public (as archipelago's are) or bearer-gated like `/metrics`? They expose wallet ↔ position; decide deliberately.
