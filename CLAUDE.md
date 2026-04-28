# MMO Networking Stack — Architecture Context

## Project Overview

Building a high-performance MMO-like multiplayer networking stack. Client is Unity (C#), server is .NET Generic Host. This project is server. Protocol is open — other client technologies must be able to implement it. Infrastructure is AWS.

---

## Transport Layer

**ENet** over UDP.

Channel semantics are enforced by convention, not by ENet itself — packet flags determine behavior per send call:
- `ENET_PACKET_FLAG_RELIABLE` — reliable ordered (ch0)
- `0` (no flags) — unreliable sequenced (ch1), stale packets silently dropped by ENet
- `ENET_PACKET_FLAG_UNSEQUENCED` — unreliable unordered

**Channel conventions:**
- ch0: reliable control flow (snapshots, events, resyncs)
- ch1: unreliable sequenced (high-frequency state updates, input)

### PeerIndex is ENet's recycled slot ID — not a stable identity

`PeerIndex` wraps `ENetPeer.ID`, which is an index into the host's fixed-size peer table. **ENet reuses the slot** as soon as a previous peer is freed, so the same `PeerIndex` value can refer to a different wallet across connect/disconnect cycles. The stable identity is the wallet address resolved during the auth handshake (`IdentityBoard.GetWalletIdByPeerIndex`).

**Implications for server code:**
- Never treat `PeerIndex` as the player identity — always dereference through `IdentityBoard` or carry the `UserId` explicitly in protocol messages.
- Any observer-side state keyed by `PeerIndex` (per-observer views, caches, baselines) must be invalidated synchronously when the underlying peer disconnects, or it will collide with the next peer that lands on that slot.
- Peer-lifecycle messages (`PlayerJoined`, `PlayerLeft`) are correctness-critical: a missed `PlayerLeft` lets the client keep a stale wallet associated with a slot and apply subsequent deltas against the wrong player.
- When writing tests that simulate reconnects, remember that ENet would reuse the slot — test the reused-ID path explicitly, not only the fresh-ID path.

---

## Authorization

**Decentraland ECDSA authentication chain**, validated entirely locally on the game server. No network call per connection.

### Identity Model

The client holds an `AuthIdentity` established during a prior browser-based login session:

```
AuthChain = [
  { type: SIGNER,          payload: "0xWALLET_ADDRESS", signature: "" },
  { type: ECDSA_EPHEMERAL, payload: "Decentraland Login\nEphemeral address: 0xEPH\nExpiration: <ISO8601>", signature: <walletSig> }
]
ephemeralIdentity = { address, privateKey, publicKey }
```

The wallet signs the ephemeral key once. The ephemeral key signs all subsequent game server connections without further wallet interaction.

### Handshake Packet

Sent on **channel 0 (reliable)** immediately after ENet transport connect:

```
{ authChain, timestamp, connectSig }
```

`connectSig` = `ECDSA_sign("connect:/server-id:TIMESTAMP:{}", ephPrivKey)`

### Server-Side Validation (local, no network call)

1. `chain[0].type == SIGNER`, `chain[0].signature == ""`  →  extract `walletAddr`
2. `chain[1].type == ECDSA_EPHEMERAL`  →  parse `ephAddr` + `expiration`; recover signer from `(payload, sig)` must equal `walletAddr`; check `expiration > now`
3. Recover signer from `(connectPayload, connectSig)` must equal `ephAddr`
4. `|now − timestamp| < 60s`  →  anti-replay
5. `server_id` in connect payload matches this instance  →  prevents cross-server token reuse

`player_id = chain[0].payload` (Ethereum wallet address, globally unique, no registration needed)

### Initial-State Seed

`HandshakeRequest.PlayerInitialState` is **the state the client authenticates with** — not a server-side fallback. The client always sends it on (re-)connect because it can't observe whether its prior session is still on the server (worker-shard isolation prevents cross-slot preservation; every fresh ENet connection lands on an empty slot). The handshake handler validates it via `FieldValidator.ValidateHandshakeInitialState` **before** transitioning to `AUTHENTICATED`; a malformed seed disconnects the peer with `INVALID_HANDSHAKE_FIELD` rather than letting half-validated state into the snapshot ring.

If `InitialState.emote_id` is set the seeded snapshot also carries an `EmoteState` with `start_tick = now − emote_start_offset_ms` (underflow-clamped) so the next `EmoteStarted` broadcast scrubs the animation forward by the elapsed-since-real-start delta. The seed is published with `Realm = null`; the first follow-up `TeleportRequest` sets the realm and inherits the seeded emote via the SnapshotBoard ledger carry-forward.

### Peer State Machine

```
CONNECTING → PENDING_AUTH → AUTHENTICATED → DISCONNECTING → [removed]
```

- `PENDING_AUTH` deadline: **30 seconds**. Non-HANDSHAKE packets silently dropped.
- Validation failure: send `HANDSHAKE_REJECT { reason }`, call `enet_peer_disconnect_later` (flushes reject before drop).
- Deadline exceeded: `enet_peer_disconnect` immediately, no message.
- Duplicate `player_id`: evict existing session, accept new one (avoids ghost connections).
- No game logic executes before `AUTHENTICATED`.

---

## Serialization

**Custom protoc plugin** generating bitwise-packed serializers from `.proto` files.

`.proto` is the single source of truth. Custom `QuantizedFloatOptions` field extension annotates float fields with `bits`, `min`, `max`. Plugin generates:

**BitWriter / BitReader** implement quantization directly:
```
encoded = round((clamp(value, min, max) - min) / (max - min) * (2^bits - 1))
decoded = (encoded / (2^bits - 1)) * (max - min) + min
```

Standard protobuf `optional` fields map to a plugin-generated field_mask on the wire — the schema stays clean, the wire encoding is compact.

---

## State Synchronization Model

**Sliding window / time-based assumption.** The server does not track per-observer confirmed baselines (no ring buffer, no ACK tracking for unreliable channel). The server diffs `current` vs `last_sent_snapshot` per observer and sends the result. If the client can't apply a delta it sends `RESYNC_REQUEST`.

**No proactive STATE_FULL mid-session.** The client drives resync, the server never anticipates it.

**Snapshot History.** The server keeps a small rolling history of snapshots per subject (`SnapshotBoard` ring buffer). Each snapshot carries positional/animation state plus nullable ledger columns (`EmoteState`, `Realm`, …) that `Publish` carries forward from the previous slot when the incoming snapshot leaves them null — so the latest ring entry is always self-sufficient, regardless of ring depth.

**Prefer extending the `SnapshotBoard` ledger over introducing parallel per-peer boards.** When adding new per-peer state that's mutated by the owning worker and read by other workers (AoI, simulation), the default is a new nullable field on `PeerSnapshot` with carry-forward in `Publish` — same pattern as `EmoteState` and `Realm`. A separate board is only justified when the data has a fundamentally different lifecycle (e.g. set once at auth and never mutated, like `IdentityBoard`) or a radically different read pattern. Don't spin up a fresh shared board just because the data is "new"; one ring seqlock + one inheritance line in `Publish` is cheaper to reason about than N parallel stores that all have to agree on lifetime and recycling.

**Intermediate snapshot scanning.** Between two simulation ticks, multiple snapshots may be published (movement, teleports, emote starts/stops). The simulation scans all intermediates from `lastSentSeq+1` to `latestSeq`, collecting the **last** of each discrete event type (teleport, emote start, emote stop). Earlier events of the same type are superseded. An emote that started and stopped in the same batch is invisible to the observer.

**Resync.** Default: always responds with `STATE_FULL`. When `Peers.ResyncWithDelta` is enabled, the server first attempts a targeted delta from the client's `knownSeq` baseline (if still in the ring), falling back to `STATE_FULL` when evicted. Configurable via `appsettings.json`, Docker env var (`Peers__ResyncWithDelta`), or GitHub manual deploy input.

**Interest management** on the server limits which players receive updates about which other players. Per-observer fan-out is the primary bandwidth concern.

---

## Message Architecture

### Client → Server

**MovementInput** (ch1, unreliable sequenced, variable while moving, 0hz while emoting)
- Full continuous state every packet: position, velocity, rotation, blend values, head IK
- Boolean state flags packed as u16 bitmask (grounded, jumping, falling, stunned, etc.)
- No piggybacked ACKs (sliding window means no ACK tracking needed)
- Quantized floats throughout
- Tiered quantization based on interest management

**EMOTE_START** (ch0, reliable)
- Emote string ID, optional duration_ms, and full PlayerState
- Server publishes a snapshot with `EmoteState` (emote ID, start tick, duration) — no separate emote board
- Client stops sending MovementInput while emoting

**EMOTE_STOP** (ch0, reliable)
- Looping emotes only; one-shots expire via server-side time check against `EmoteState.DurationMs`
- Server publishes a stop snapshot (EmoteId=null, StopReason=Cancelled) preserving the subject's position

**TELEPORT_REQUEST** (ch0, reliable)
- Client-initiated teleport (e.g. triggered by game logic)
- Server validates and rebroadcasts as TELEPORT to all observers

**RESYNC_REQUEST** (ch0, reliable)
- Sent when a received STATE_DELTA can't be applied (gap in seq)
- Server responds with STATE_FULL (or targeted delta when `Peers.ResyncWithDelta` is enabled)

### Server → Client

**STATE_FULL** (ch0, reliable)
- Full snapshot of a subject's state
- Sent on zone entry or in response to RESYNC_REQUEST

**STATE_DELTA** (ch1, unreliable sequenced, per server tick)
- Diff from last_sent_snapshot for each observer/subject pair
- field_mask (from optional field presence) suppresses unchanged continuous fields
- state_flags always present regardless of mask
- Quantized floats, same ranges as MovementInput

**EMOTE_STARTED** (ch0, reliable, broadcast to interest set)
- Emote string ID, sequence, server_tick, and full PlayerState
- PlayerState sent reliably because no further position updates will arrive during the emote
- Observers use server_tick to scrub animation forward by transit latency
- Only the last emote start per batch is broadcast; earlier ones superseded by the latest

**EMOTE_STOPPED** (ch0, reliable, broadcast to interest set)
- Reason: completed (one-shot duration expired) or cancelled (client sent EMOTE_STOP)
- Carries sequence and full PlayerState so the client can snap to the correct position on resume
- Client resumes MovementInput only after receiving this (gates resume on server clock)

**TELEPORT** (ch0, reliable, broadcast to interest set)
- Server-authoritative teleport position with server_tick
- Receiver clears interpolation buffer and snaps to position

---

## Key Design Decisions & Rationale

**No movement lock on server during emotes.** Client is responsible for not sending MovementInput while emoting. Server is a relay for emote events, not a movement authority.

**No server-side scene simulation.** The server relays and validates client-reported positions. It cannot compute positions independently.

**Client drives resync.** The server never proactively fires STATE_FULL when a baseline goes stale. The gap detection lives on the client (seq number check), which triggers RESYNC_REQUEST.

**Unreliable input, not reliable.** Movement input on the unreliable channel avoids head-of-line blocking. A retransmitted stale position is worse than a skipped one. 3-tick redundancy recovers from loss without retransmission overhead.

**state_flags always present in STATE_DELTA.** Boolean transitions (jump, land, fall) drive animation events. Missing one costs more than the 2 bytes it takes to always include the full state.

**Teleport as a separate message, not an is_instant flag.** Teleports are discrete events, not a property of continuous movement. Keeping them as a dedicated reliable message guarantees the interpolation-skip instruction arrives before subsequent position updates.

**server_tick is a single unified clock** across all messages (STATE_DELTA, EMOTE_STARTED, EMOTE_STOPPED, TELEPORT). Client uses it for animation scrubbing and dead reckoning. `peer->roundTripTime` (available on both client and server via ENet) provides latency without requiring client_tick fields in packets.

**Emote state inlined into PeerSnapshot.** `EmoteState` (emote ID, start tick, duration, stop reason) is a nullable struct on `PeerSnapshot`, stored in the ring buffer alongside positional data. No separate emote board — the snapshot ring is the single source of truth. EmoteStartHandler writes emote metadata directly into the snapshot. EmoteStopHandler publishes a stop snapshot. One-shot emote expiry is computed lazily from the view's cached duration at observation time, not eagerly mutated.

**Simulation loop: scan → broadcast → stop → delta.** Each tick, the simulation scans intermediate snapshots and collects the last teleport, emote start, and emote stop. Only the final event of each type is broadcast — intermediate positions or superseded emotes are discarded. An emote that started and stopped in the same batch is invisible to the observer. Discrete events (teleport, emote start) suppress the unreliable delta for that tick to prevent baseline races. Emote stop does not suppress delta — the client needs the position update on resume.

**Protobuf optional fields = field_mask on wire.** The schema expresses intent with `optional`. The plugin generates a compact bitmask for the wire. These are the same concept at different layers — the plugin bridges them.

**Snapshot publishing goes through `PeerSnapshotPublisher`.** Every handler that mutates peer state (`PlayerStateInputHandler`, `EmoteStartHandler`, `TeleportHandler`, the handshake initial-state seed) calls one of two methods on the publisher: `PublishFromPlayerState(from, state, EmoteInput?)` for `PlayerState`-shaped events, or `PublishTeleport(from, parcelIndex, localPosition, realm)` for teleports. The publisher owns Seq numbering (`LastSeq + 1`), parcel→global decoding, head-IK lifting from `PlayerState`, the `SnapshotBoard.Publish` + `SpatialGrid.Set` pair, and emote-ledger bookkeeping (`StartSeq` is stamped to the new snapshot's `Seq`, `StartTick` defaults to `ServerTick` when caller leaves it null). Don't reconstruct a `PeerSnapshot` inline in a handler — add it to the publisher.

`EmoteInput(EmoteId, DurationMs?, StartTick?)` is the caller-facing emote-start descriptor. Callers pass only what's semantically theirs (the emote identity, its duration, optionally a backdated start tick for reconnect resume); ledger fields like `StartSeq` are not part of the API. `EmoteStart` callers omit `StartTick` (defaults to "started right now"); the handshake reconnect path passes a backdated `StartTick` so observers scrub forward by the elapsed-since-real-start delta.

---

## RTT

ENet maintains `peer->roundTripTime` automatically on both client and server sides via the reliable channel ACK flow. No manual measurement needed. Client uses `peer->roundTripTime / 2` as one-way latency estimate for animation scrubbing on emote start.

---

## Code Convention

Authoritative source: `DCLPulse.sln.DotSettings` (Rider code style settings checked into the repo).

Key rules:
- **Instance fields:** camelCase, no prefix — `messagePipe`, `workerCount`
- **Constants and static readonly fields:** UPPER_SNAKE_CASE — `SECTION_NAME`, `COUNT`, `RELIABLE`, `TIER_0`
- **Types:** PascalCase — `PeersManager`, `ENetChannel`
- **Local variables and parameters:** camelCase — `peerIndex`, `stoppingToken`
- **Primary constructors** for DI when constructor body is trivial; regular constructors when initialization is complex
- **File-scoped namespaces** — `namespace Pulse.X;`
- **`var`** when type is clear from context

When in doubt, check 1–2 nearby files in the same directory.

## Tests Approach

- Use NSubstitute instead of Fake/Null implementations
- Don't mention line numbers in comments as they can change any time

## Worker-shard isolation rule

`PeersManager` shards peers across workers by `PeerIndex.Value % workerCount`. Every worker owns its own `peerStates` dict and its own `observerViews`; the owning worker is the **only** thread that reads or writes those structures. Cross-worker state migration and direct-message-passing between workers are not supported and must not be introduced — ad-hoc handshake/reclamation schemes that try to move a peer's state from one worker's dict to another's race the owning worker's simulation loop (DISCONNECTING cleanup, sweep, message drain) and silently destroy live state.

All cross-worker coordination goes through the one existing channel: the ENet thread writes `MessagePipe.incomingChannel` (lifecycle + data events), the `PeersManager` router fans those out to `workerChannels[shard]`, and each worker processes its own channel sequentially. If a feature seems to need cross-worker orchestration, the right fix is to either keep the coordination at the transport/allocator layer (which is already shared) or route decisions through the incoming-event pipeline so they land on the target worker in order.

Concrete consequences:
- Same-wallet reconnect always gets a **fresh** server-allocated `PeerIndex` today. We do not rekey the transport to reuse the prior `PeerIndex` — doing so would require cross-worker rekey, which this rule forbids.
- Observer-facing effect without rekey: after a same-wallet reconnect, observers briefly hold two views for the same wallet — the stale `PeerIndex` (awaiting the next `SweepStaleViews` pass, up to ~2 × `SWEEP_INTERVAL` × `BaseTickMs` ≈ 10 s) and the fresh `PeerIndex` for the new session. Clients that key avatars by wallet overwrite transparently; clients that key by `subject_id` see a short-lived duplicate until the `PlayerLeft` from the sweep arrives. No state corruption — only a visual blemish on the reconnect path.
- Different-wallet on a recycled ENet slot: the allocator's pending-recycle already prevents the server from issuing the same `PeerIndex` to a different wallet within the grace window, so this case does not produce aliased observer views; the original bug is fixed.

## PeerSimulation — method decoupling

`PeerSimulation` is on the hot path and already long. New logic added to it must go into its own private method — do not inline new behavior into existing methods. Keep each method focused on a single concern (e.g. tier gating, delta computation, aliasing detection, profile announcement). The orchestrator `ProcessVisibleSubjects` should read as a short sequence of named calls, not a wall of conditionals. This keeps the per-subject control flow legible and makes it possible to test or reason about each concern in isolation.

## Docker — Deployment & Debugging

Three Dockerfiles:
- `src/DCLPulse/Dockerfile` — production (Release, lean runtime image)
- `src/DCLPulse/Dockerfile.dev-debug` — Fargate dev debug deploy (Debug build + vsdbg + sshd + RiderRemoteDebugger pre-installed)
- `Dockerfile.debug` + `docker-compose.debug.yml` — local docker-compose debugging

Deploy pipeline: `main` push builds `Dockerfile` → dev. Manual **Deploy Dev (Debug)** action builds `Dockerfile.dev-debug` → dev. Release tag builds `Dockerfile` → prod.

For full debugging workflows (local + remote Fargate, Rider setup, logpoints, ports), see [docs/debugging.md](docs/debugging.md). Bastion/tunnel specifics are in the `decentraland/playbooks` repo (internal access only).

---

## Files / Components Expected

- `decentraland/common/options.proto` — defines `QuantizedFloatOptions` and `BitPackedOptions` as protobuf field extensions
- `decentraland/pulse/pulse_client.proto` — client→server messages and the `ClientMessage` envelope
- `decentraland/pulse/pulse_server.proto` — server→client messages, the `ServerMessage` envelope, and the only quantized message (`PlayerStateDeltaTier0`)
- `decentraland/pulse/pulse_shared.proto` — types referenced by both directions (`PlayerState`, `GlideState`, `PlayerAnimationFlags`); imported by both client and server protos
- `protoc-gen-bitwise` — Python plugin, reads `CodeGeneratorRequest`, emits C# serializers
- `BitWriter` / `BitReader` (C#) — bit packing + quantization (`WriteQuantizedFloat` / `ReadQuantizedFloat`), used by generated C# serializers

## MetaForge — Test Account & Identity Toolkit

**Local copy:** sibling directory `../MetaForge` (same parent as this repo checkout)

MetaForge is a CLI toolkit (.NET 10, self-contained binary) used by `DCLPulseTestClient` for test account management, identity creation, and profile deployment. It provides the Decentraland ECDSA auth chain that the test client needs to connect to the game server.

### How DCLPulseTestClient uses MetaForge

The test client shells out to the `metaforge` CLI via `MetaForge.RunCommandAsync()` (`src/DCLPulseTestClient/MetaForge.cs`). Three integration points:

1. **Account creation:** `metaforge account create <name> --skip-update-check --skip-auto-login` — generates a BIP39 wallet, derives Ethereum address, deploys a default profile to Catalyst
2. **Auth chain signing:** `metaforge account chain <name> --method connect --path / --metadata {} --json` — returns the 3-link auth chain (SIGNER → ECDSA_EPHEMERAL → ECDSA_SIGNED_ENTITY) that `MetaForgeAuthenticator` formats into `x-identity-auth-chain-{n}` headers for the handshake
3. **Profile fetching:** `metaforge account info <name> --json` — returns profile metadata (eth address, version, emotes) that `MetaForgeProfileGateway` parses

### Key MetaForge CLI commands

```bash
metaforge account create [name] [--skip-auto-login] [--env org|zone]
metaforge account chain <name> --method <m> --path <p> --metadata <json> [--json]
metaforge account info [name] [--json]
metaforge account list
metaforge account remove [name] [--all]
metaforge account steal-identity <name> [--id <n>] [--env org|zone]
metaforge explorer install|run|logs|prefs|backup|test [...]
metaforge mob auth|update-addresses|run <world> [--log-events]
metaforge launcher install|run|uninstall|log
```

### MetaForge project structure

```
MetaForge/
├── MetaForgeCLI/              # Main CLI application
│   ├── Auth/                  # Identity.cs, AuthChain.cs — wallet + ephemeral key delegation
│   ├── Wallet/                # WalletService.cs — BIP39 HD wallet (m/44'/60'/0'/0/0)
│   ├── Commands/              # Account/, Explorer/, Launcher/, Mob/ command groups
│   ├── Services/              # CatalystService, SignedHttpClient, ExplorerVersionService, AltTesterService, etc.
│   ├── Persistency/           # AccountStore.cs (JSON), MobConfigStore.cs (.env)
│   └── Config/                # EnvironmentConfig.cs — org vs zone environment URLs
└── MoB/                       # LiveKit bot controller (BotManager, LiveKitBot, LiveTui)
```

### Environments

| Environment | Auth API | Catalyst |
|---|---|---|
| `org` (default) | `https://auth-api.decentraland.org` | `https://peer.decentraland.org` |
| `zone` | `https://auth-api.decentraland.zone` | `https://peer.decentraland.zone` |

---

## Build instructions

- The project targets .NET 10. The SDK is installed at `~/.dotnet` (user-local). Always prefix all `dotnet` commands with the environment override — no probing needed:
  ```bash
  DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false
  ```
- The solution file is `src/DCLPulse/DCLPulse.sln` — always pass it explicitly since it's not in the repo root.
- Use `-p:GenerateProto=false` unless the user explicitly asks to regenerate proto files.
- To run tests: `DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`