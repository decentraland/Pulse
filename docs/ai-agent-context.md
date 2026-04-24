# AI Agent Context

**Service Purpose:** Authoritative avatar synchronization server for Decentraland. Relays avatar state (position, rotation, animation, emotes, teleports) between players within a single Archipelago island over UDP/ENet. One Pulse instance = one island.

**Role in the real-time layer:** Pulse is one of two parallel connections a client maintains (the other is LiveKit). It handles high-frequency avatar state only — it has no awareness of scene entities, voice, or CRDT. The client's Pulse address is currently hardcoded; Archipelago does not yet return a dynamic Pulse endpoint.

---

## Transport

**Protocol:** UDP / ENet on port 7777
**Load balancer:** AWS NLB (L4) — mandatory, ALB does not support UDP. Sticky 5-tuple routing ensures a client always reaches the same Pulse instance.

**ENet channels:**

| Channel | Reliability | Use |
| --- | --- | --- |
| 0 | Reliable ordered | Handshake, emotes, teleports, resync responses, join/leave events |
| 1 | Unreliable sequenced | High-frequency position/animation deltas (~20 msg/s at Tier 0) |
| 2 | Unreliable unsequenced | Declared (`ENetChannel.UNRELIABLE_UNSEQUENCED`), no message currently targets it |

---

## Authentication

Local ECDSA auth chain validation — no network call per connection.

```
CONNECTING → PENDING_AUTH → AUTHENTICATED → DISCONNECTING → [removed]
```

- Client sends `{ authChain, timestamp, connectSig }` on channel 0 immediately after ENet connect
- Server validates locally: chain structure → ephemeral key expiry → connectSig → timestamp within 60s → server_id match
- `PENDING_AUTH` deadline: 30 seconds. Non-handshake packets silently dropped.
- Auth failure: `HANDSHAKE_REJECT { reason }` then `enet_peer_disconnect_later`
- `player_id = chain[0].payload` (Ethereum wallet address)

### Hardening layers

Defense-in-depth, all local, all fail-closed. Each has a dedicated class under `Transport/Hardening/` or `Messaging/Hardening/`:

- **Pre-auth admission** (`PreAuthAdmission`) — global `PreAuthBudget` reserves capacity for AUTHENTICATED peers; per-source-IP `MaxConcurrentPreAuthPerIP` prevents single-IP floods.
- **Handshake replay cache** (`HandshakeReplayPolicy`) — sliding-window `(wallet, timestamp)` rejection within `PendingAuthCleanTimeoutMs`. Sweeps opportunistically at 50% capacity.
- **Handshake attempt cap** (`HandshakeAttemptPolicy`) — per-peer counter bounds repeated ECDSA recoveries.
- **Field validation** (`FieldValidator`) — bounds / NaN / Infinity / length checks on all client-supplied fields; violation disconnects with a specific `DisconnectReason`.
- **Movement input rate limit** (`MovementInputRateLimiter`) — per-peer min-interval gate; violation → instant disconnect with `INPUT_RATE_EXCEEDED`.
- **Discrete event rate limit** (`DiscreteEventRateLimiter`) — token-bucket on `EmoteStart` / `EmoteStop` / `Teleport`; discard-on-violation, not backpressure.

---

## State Synchronization

**Sliding window / time-based assumption.** No ACK tracking for the unreliable channel. Server diffs `current` vs `last_sent_snapshot` per observer and sends the result. If the client can't apply a delta it sends `RESYNC_REQUEST`.

**3-tier spatial LOD** (base tick 50 ms, cadences from `PeerOptions.SimulationSteps = {50, 100, 200}`):

| Tier | Distance | Update frequency |
| --- | --- | --- |
| 0 | ≤ 20m | Every tick (~20/s) |
| 1 | ≤ 50m | Every 2nd tick (~10/s) |
| 2 | ≤ 100m | Every 4th tick (~5/s) |

Players outside 100m (`SpatialAreaOfInterestOptions.MaxRadius`) receive no updates.

**Realm partitioning.** `PeerSnapshot.Realm` gates visibility inside the area-of-interest collectors: observers only see subjects whose `Realm` string matches exactly. A single Pulse instance transparently hosts multiple realms that never see each other.

**Snapshot history:** Server keeps a small rolling ring of snapshots per subject (`SnapshotBoard`). `RESYNC_REQUEST` default response is `STATE_FULL`. When `Peers.ResyncWithDelta` is enabled, the server first attempts a targeted delta from the client's `knownSeq` baseline and falls back to `STATE_FULL` when that seq has been evicted from the ring.

**SnapshotBoard ledger.** Each snapshot carries positional/animation state plus nullable columns (`EmoteState`, `Realm`, …) that `Publish` carries forward from the previous slot when the incoming snapshot leaves them null — the latest ring entry is always self-sufficient. New per-peer state should extend this ledger (nullable `PeerSnapshot` column + carry-forward) rather than spawn parallel shared boards.

**Other per-peer boards.** `IdentityBoard` (`PeerIndex ↔ Wallet`, canonical identity source) and `ProfileBoard` (per-peer profile version, drives `ProfileAnnouncement` fan-out). Both are zeroed on cleanup.

**AoI implementation.** Default wiring is `SpatialHashAreaOfInterest` backed by `SpatialGrid` (Morton-encoded cells, 9-cell query neighborhood). `SpatialAreaOfInterest` is the linear-scan fallback used in tests.

**Simulation tick** (per worker, per peer it owns): scan intermediate snapshots since `lastSentSeq` and collapse to the last teleport / emote-start / emote-stop; broadcast discrete events; suppress the unreliable delta on teleport or emote-start (avoids baseline races); otherwise compute and send `PlayerStateDelta`.

**Stale-view sweep.** Every `SWEEP_INTERVAL` (≈100 ticks, ~5 s) `PeerSimulation.SweepStaleViews` prunes observer views for no-longer-visible subjects and emits `PlayerLeft`. Bounds memory and closes the same-wallet-reconnect "two views" window.

**Self-mirror.** When `Peers.SelfMirrorEnabled=true`, each peer receives its own state as if from another peer under `SELF_MIRROR_WALLET_ID` at tier `SelfMirrorTier`. Client-side animation testing aid.

---

## Message Reference

Proto-level names: see the `ClientMessage` / `ServerMessage` `oneof message` in `decentraland/pulse/*.proto`. The tables below use on-wire message names.

### Client → Server

| Message | Channel | Description |
| --- | --- | --- |
| `Handshake` | 0 (reliable) | Auth chain + timestamp + connectSig. First packet after transport connect. |
| `Input` (`MovementInput`) | 1 (unreliable sequenced) | Full continuous state: position, velocity, rotation, blend values, head IK, state flags (u16 bitmask). Sent ~20/s while moving; paused during emotes. |
| `Resync` (`ResyncRequest`) | 0 (reliable) | Sent when a `PlayerStateDelta` can't be applied (seq gap). Carries `SubjectId, KnownSeq`. |
| `ProfileAnnouncement` (`ProfileVersionAnnouncement`) | 0 (reliable) | Client announces a new profile version; server rebroadcasts to observers as `PlayerProfileVersionsAnnounced`. |
| `EmoteStart` | 0 (reliable) | `EmoteId, DurationMs (optional), PlayerState`. Client stops sending `Input` while emoting. |
| `EmoteStop` | 0 (reliable) | Looping emotes only — one-shot emote expiry is computed lazily at observation time from the cached duration, not by a server-side timer. |
| `Teleport` (`TeleportRequest`) | 0 (reliable) | Client-initiated teleport (`ParcelIndex, Position, Realm`). Server validates and rebroadcasts as `Teleported`. |

### Server → Client

| Message | Channel | Description |
| --- | --- | --- |
| `Handshake` | 0 (reliable) | Auth accept/reject response. On reject, followed by `enet_peer_disconnect_later`. |
| `PlayerJoined` | 0 (reliable, broadcast) | A peer entered the observer's interest set. |
| `PlayerLeft` | 0 (reliable, broadcast) | A peer left the observer's interest set (distance, realm change, disconnect, or stale-view sweep). |
| `PlayerStateFull` | 0 (reliable) | Full snapshot of a subject. Sent on zone entry or in response to `Resync`. |
| `PlayerStateDelta` | 1 (unreliable sequenced) | Delta from `last_sent_snapshot`. Field mask suppresses unchanged fields. State flags always present. |
| `PlayerProfileVersionsAnnounced` | 0 (reliable, broadcast) | Fan-out of `ProfileAnnouncement` to observers. |
| `EmoteStarted` | 0 (reliable, broadcast) | `SubjectId, Sequence, ServerTick, EmoteId, PlayerState`. Full `PlayerState` sent reliably because no further position updates arrive during the emote. |
| `EmoteStopped` | 0 (reliable, broadcast) | `SubjectId, Sequence, ServerTick, Reason, PlayerState`. Reason = completed (one-shot duration expired) or cancelled (client `EmoteStop`). `PlayerState` lets the client snap to the correct position on resume. |
| `Teleported` | 0 (reliable, broadcast) | Authoritative position + `ServerTick`. Client clears interpolation buffer and snaps. |

---

## Serialization

Custom `protoc` plugin (`protoc-gen-bitwise`) generates bitwise-packed C# serializers from `.proto` files.

- Float fields annotated with `bits`, `min`, `max` → quantized encoding: `encoded = round((clamp(v, min, max) - min) / (max - min) * (2^bits - 1))`
- `optional` proto fields → plugin-generated field mask on the wire (compact, schema stays clean)
- `BitWriter` / `BitReader` implement quantization directly

---

## Scaling

One Pulse instance per island. Island count grows with concurrent players (Archipelago creates new islands as needed). A single instance also shards across realms internally — see "Realm partitioning" above.

```
AWS NLB (L4 · UDP · sticky 5-tuple)
        │
   Target Group
   ┌────┼────┐
Pulse-A  Pulse-B  Pulse-C ...
≤4095    ≤4095    ≤4095 peers (`MaxPeers`, ENet-bounded)
```

**Worker-shard isolation.** Within a single instance, `PeersManager` shards peers across workers by `PeerIndex.Value % workerCount`. Each worker owns its own `peerStates` and `observerViews` and is the only thread that touches them. Cross-worker coordination goes through a single channel: the ENet thread writes `MessagePipe.incomingChannel`, `PeersManager` fans out to `workerChannels[shard]`, each worker drains its own channel sequentially. No ad-hoc cross-worker state migration.

**Peer lifecycle & slot recycling.** `PeerIndexAllocator` runs a three-phase lifecycle: `Allocate` → `MarkPending` (on ENet disconnect) → `Release` (after per-peer board cleanup). The grace window is `DisconnectionCleanTimeoutMs` and must exceed `SWEEP_INTERVAL × BaseTickMs` so observers emit `PlayerLeft` before the slot is reusable. A mid-session wallet-mismatch check in `PeerSimulation` is the fallback when a slot aliases and an observer still holds the old wallet.

**Fan-out ceiling:** At dense events where all peers are within Tier 0, fan-out is O(N²) per tick. No shedding mechanism exists.

---

## Observability

- **Instruments:** `PulseMetrics` (counters / gauges via `System.Diagnostics.Metrics`), zero-alloc `Interlocked` updates on the hot path.
- **Collector:** `MeterListenerMetricsCollector` subscribes and snapshots on demand.
- **Prometheus:** `PrometheusFormatter` emits text-exposition format; `HttpService` serves `/metrics` with bearer-token auth.
- **Console dashboard:** `ConsoleDashboard` (TUI on a dedicated thread) polls snapshots every 500 ms — rates, percentiles, sparklines, per-hardening counters.

---

## Technology Stack

- Runtime: .NET 10
- Language: C#
- Transport: ENet (native, via managed wrapper)
- Serialization: Custom bitwise protoc plugin + BitWriter/BitReader
- Crypto: Local ECDSA validation (no network call)
- Infrastructure: AWS NLB (L4, UDP)
- SDK: Installed at `~/.dotnet` (user-local)

**Build:**
```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

**Tests:**
```bash
DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
```

The solution file is `src/DCLPulse/DCLPulse.sln` — always pass it explicitly (not in repo root). Use `-p:GenerateProto=false` unless explicitly regenerating proto files.

---

## Code Conventions

See `DCLPulse.sln.DotSettings` (Rider settings, checked in).

- Instance fields: `camelCase`, no prefix — `messagePipe`, `workerCount`
- Constants / static readonly: `UPPER_SNAKE_CASE` — `SECTION_NAME`, `RELIABLE`
- Types: `PascalCase` — `PeersManager`, `ENetChannel`
- Local variables / parameters: `camelCase`
- Primary constructors for DI when trivial; regular constructors when initialization is complex
- File-scoped namespaces: `namespace Pulse.X;`
- `var` when type is clear from context

**Tests:** Use NSubstitute. Do not mention line numbers in comments.

---

## Design Decisions

- **No movement lock during emotes.** Client is responsible for not sending `Input` while emoting. Server relays emote events, not a movement authority.
- **No server-side scene simulation.** Server relays and validates client-reported positions only. Cannot compute positions independently.
- **Client drives resync.** Server never proactively sends `PlayerStateFull` when baseline goes stale — client detects the seq gap and sends `Resync`.
- **Unreliable channel for movement input.** Avoids head-of-line blocking. A retransmitted stale position is worse than a dropped one.
- **`state_flags` always present in `PlayerStateDelta`.** Boolean transitions (jump, land, fall) drive animation events; missing one is more expensive than the 2 bytes.
- **`server_tick` unified clock.** Millisecond wall time from `MonotonicTimeProvider`, stamped by handlers on every snapshot. Used across all messages for animation scrubbing, dead reckoning, and `EmoteCompleter` expiry checks. ENet `peer->roundTripTime / 2` provides the one-way latency estimate. Not a simulation-tick counter.
- **Emote state inlined into `PeerSnapshot`.** `EmoteState` (emote ID, start tick, duration, stop reason) is a nullable column on the snapshot ring, not a separate board. One-shot expiry is finalized by `EmoteCompleter` on the owning worker's loop, which publishes a `Completed` stop snapshot when `now - StartTick >= DurationMs`; observers pick it up through the normal intermediate-snapshot scan.
- **`PeerIndex` is not an identity.** It's ENet's recycled slot ID (index into the host's peer table). Stable identity is the wallet address resolved during auth and held by `IdentityBoard`. Observer-side state keyed by `PeerIndex` must be invalidated synchronously when the underlying peer disconnects, or it aliases the next peer that lands on that slot.

---

## Known Architectural Issues

- **Pulse address is hardcoded in the client.** No dynamic assignment from Archipelago. Cannot route different islands to different Pulse instances at the application level; NLB handles it by sticky 5-tuple but Pulse has no awareness of island boundaries.
- **Pulse and Hammurabi are blind to each other.** Pulse knows avatar positions; Hammurabi owns scene entity state. No interface between them — position-based server-side logic (collision, triggers) is not enforceable.
- **No graceful drain on deploy.** All ENet connections drop simultaneously on instance restart.
- **Client-reported positions are fully trusted.** No server validation against scene geometry.
