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
| 1 | Unreliable sequenced | High-frequency position/animation deltas (~20 msg/s) |
| 2 | Unreliable unsequenced | Reserved |

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

---

## State Synchronization

**Sliding window / time-based assumption.** No ACK tracking for the unreliable channel. Server diffs `current` vs `last_sent_snapshot` per observer and sends the result. If the client can't apply a delta it sends `RESYNC_REQUEST`.

**3-tier spatial LOD:**

| Tier | Distance | Update frequency |
| --- | --- | --- |
| 0 | ≤ 20m | Every tick (~20/s) |
| 1 | ≤ 50m | Every 2nd tick |
| 2 | ≤ 100m | Every 4th tick |

Players outside 100m receive no updates.

**Snapshot history:** Server keeps a small rolling ring of snapshots per subject. `RESYNC_REQUEST` with a seq still in the ring gets a targeted delta instead of a full `STATE_FULL`.

---

## Message Reference

### Client → Server

| Message | Channel | Description |
| --- | --- | --- |
| `MovementInput` | 1 (unreliable sequenced) | Full continuous state: position, velocity, rotation, blend values, head IK, state flags (u16 bitmask). Sent ~20/s while moving; paused during emotes. |
| `EMOTE_START` | 0 (reliable) | Emote string ID. Client stops sending `MovementInput` while emoting. |
| `EMOTE_STOP` | 0 (reliable) | Looping emotes only — one-shots are terminated by server timer. |
| `TELEPORT_REQUEST` | 0 (reliable) | Client-initiated teleport. Server validates and rebroadcasts. |
| `RESYNC_REQUEST` | 0 (reliable) | Sent when a `STATE_DELTA` can't be applied (seq gap). |

### Server → Client

| Message | Channel | Description |
| --- | --- | --- |
| `STATE_FULL` | 0 (reliable) | Full snapshot of a subject. Sent on zone entry or in response to `RESYNC_REQUEST`. |
| `STATE_DELTA` | 1 (unreliable sequenced) | Delta from `last_sent_snapshot`. Field mask suppresses unchanged fields. State flags always present. |
| `EMOTE_STARTED` | 0 (reliable, broadcast) | Emote ID, type, server_tick, anchor position. Anchor sent reliably — no further position updates arrive during the emote. |
| `EMOTE_STOPPED` | 0 (reliable, broadcast) | Reason: completed (one-shot timer) or cancelled (client EMOTE_STOP). |
| `TELEPORT` | 0 (reliable, broadcast) | Authoritative position + server_tick. Client clears interpolation buffer and snaps. |

---

## Serialization

Custom `protoc` plugin (`protoc-gen-bitwise`) generates bitwise-packed C# serializers from `.proto` files.

- Float fields annotated with `bits`, `min`, `max` → quantized encoding: `encoded = round((clamp(v, min, max) - min) / (max - min) * (2^bits - 1))`
- `optional` proto fields → plugin-generated field mask on the wire (compact, schema stays clean)
- `BitWriter` / `BitReader` implement quantization directly

---

## Scaling

One Pulse instance per island. Island count grows with concurrent players (Archipelago creates new islands as needed).

```
AWS NLB (L4 · UDP · sticky 5-tuple)
        │
   Target Group
   ┌────┼────┐
Pulse-A  Pulse-B  Pulse-C ...
≤4095    ≤4095    ≤4095 peers (ENet hard limit)
```

**Fan-out ceiling:** At dense events where all peers are within Tier 0, fan-out is O(N²) per tick. No shedding mechanism exists.

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

- **No movement lock during emotes.** Client is responsible for not sending `MovementInput` while emoting. Server relays emote events, not a movement authority.
- **No server-side scene simulation.** Server relays and validates client-reported positions only. Cannot compute positions independently.
- **Client drives resync.** Server never proactively sends `STATE_FULL` when baseline goes stale — client detects the seq gap and sends `RESYNC_REQUEST`.
- **Unreliable channel for movement input.** Avoids head-of-line blocking. A retransmitted stale position is worse than a dropped one.
- **`state_flags` always present in `STATE_DELTA`.** Boolean transitions (jump, land, fall) drive animation events; missing one is more expensive than the 2 bytes.
- **`server_tick` unified clock.** Used across all messages for animation scrubbing and dead reckoning. ENet `peer->roundTripTime / 2` provides one-way latency estimate without manual measurement.

---

## Known Architectural Issues

- **Pulse address is hardcoded in the client.** No dynamic assignment from Archipelago. Cannot route different islands to different Pulse instances at the application level; NLB handles it by sticky 5-tuple but Pulse has no awareness of island boundaries.
- **Pulse and Hammurabi are blind to each other.** Pulse knows avatar positions; Hammurabi owns scene entity state. No interface between them — position-based server-side logic (collision, triggers) is not enforceable.
- **No graceful drain on deploy.** All ENet connections drop simultaneously on instance restart.
- **Client-reported positions are fully trusted.** No server validation against scene geometry.
