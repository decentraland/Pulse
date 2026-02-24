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

**Interest management** on the server limits which players receive updates about which other players. Per-observer fan-out is the primary bandwidth concern.

---

## Message Architecture

### Client → Server

**MovementInput** (ch1, unreliable sequenced, variable while moving, 0hz while emoting)
- Full continuous state every packet: position, velocity, rotation, blend values, head IK
- Boolean state flags packed as u16 bitmask (grounded, jumping, falling, stunned, etc.)
- 3-tick redundancy: last 3 ticks bundled in each packet to recover from loss without retransmission
- No piggybacked ACKs (sliding window means no ACK tracking needed)
- Quantized floats throughout
- Tiered quantization based on interest management

**EMOTE_START** (ch0, reliable)
- Emote string ID, emote type (one_shot / looping) is not transmitted, it is resolved from the DTO on the client side
- Client stops sending MovementInput while emoting

**EMOTE_STOP** (ch0, reliable)
- Looping emotes only; one-shots are terminated by the server timer

**TELEPORT_REQUEST** (ch0, reliable)
- Client-initiated teleport (e.g. triggered by game logic)
- Server validates and rebroadcasts as TELEPORT to all observers

**RESYNC_REQUEST** (ch0, reliable)
- Sent when a received STATE_DELTA can't be applied (gap in seq)
- Server responds with STATE_FULL

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
- Emote string ID, type, server_tick, and piggybacked anchor position (Vec3)
- Anchor position sent reliably because no further position updates will arrive during the emote
- Observers use server_tick to scrub animation forward by transit latency

**EMOTE_STOPPED** (ch0, reliable, broadcast to interest set)
- Reason: completed (one-shot timer) or cancelled (client sent EMOTE_STOP)
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

**Protobuf optional fields = field_mask on wire.** The schema expresses intent with `optional`. The plugin generates a compact bitmask for the wire. These are the same concept at different layers — the plugin bridges them.

---

## RTT

ENet maintains `peer->roundTripTime` automatically on both client and server sides via the reliable channel ACK flow. No manual measurement needed. Client uses `peer->roundTripTime / 2` as one-way latency estimate for animation scrubbing on emote start.

---

## Docker — Deployment & Debugging

### Production Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.sln ./
COPY src/ ./src/
RUN dotnet restore
RUN dotnet publish src/GameServer/GameServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 7777/udp
ENTRYPOINT ["dotnet", "GameServer.dll"]
```

### Debug Dockerfile (`Dockerfile.debug`)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS debug
WORKDIR /app
RUN apt-get update && apt-get install -y curl unzip procps && \
    curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /vsdbg && \
    rm -rf /var/lib/apt/lists/*
COPY . .
EXPOSE 7777/udp
ENTRYPOINT ["dotnet", "run", "--project", "src/GameServer/GameServer.csproj", "--configuration", "Debug"]
```

### docker-compose.debug.yml

```yaml
services:
  game-server:
    build:
      context: .
      dockerfile: Dockerfile.debug
    container_name: game-server-debug
    ports:
      - "7777:7777/udp"
      - "8080:8080"
    volumes:
      - ./src:/app/src
    environment:
      DOTNET_ENVIRONMENT: Development
      Logging__LogLevel__Default: Debug
    cap_add:
      - SYS_PTRACE         # mandatory for vsdbg
    security_opt:
      - seccomp:unconfined # mandatory for vsdbg
```

### .csproj Debug Symbols

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Debug'">
  <DebugType>full</DebugType>
  <DebugSymbols>true</DebugSymbols>
  <Optimize>false</Optimize>
</PropertyGroup>
```

### Rider Remote Debugging

Selected approach: **Rider → Docker Attach to .NET process** (not SSH remote).

1. Start container first: `docker compose -f docker-compose.debug.yml up --build`
2. Wait for server to log that it is listening on port 7777
3. In Rider: **Run → Edit Configurations → + → .NET Attach to Remote Process**
   - Connection type: Docker container
   - Container: `game-server-debug`
   - vsdbg path: `/vsdbg`
4. Path mapping: local `./src` ↔ container `/app/src` (resolved automatically via volume mount; set manually if Rider misses it)

**Logpoints over breakpoints** for networking code — right-click gutter → Add Logpoint. Evaluates expression and logs to Debug console without pausing the ENet tick loop or disconnecting clients.

### IP Addresses (Local)

| Scenario | Address |
|---|---|
| Unity client → game server (same machine) | `127.0.0.1:7777` |
| Container internal IP (changes each run) | `docker inspect game-server-debug \| grep IPAddress` |
| Container → container (same compose) | Use service name, e.g. `game-server:7777` |
| Container → host machine service | `host.docker.internal` (Linux: add `extra_hosts: - "host.docker.internal:host-gateway"`) |

### Quick Reference

| Action | Command |
|---|---|
| Start debug container | `docker compose -f docker-compose.debug.yml up --build` |
| Tail logs | `docker logs -f game-server-debug` |
| Rebuild after Dockerfile change | `docker compose -f docker-compose.debug.yml up --build --force-recreate` |
| Stop | `docker compose -f docker-compose.debug.yml down` |
| Push to ECR | `aws ecr get-login-password \| docker login --username AWS --password-stdin <account>.dkr.ecr.<region>.amazonaws.com` |

---

## Files / Components Expected

- `decentraland/common/options.proto` — defines `QuantizedFloatOptions` and `BitPackedOptions` as protobuf field extensions
- `movement.proto`, `emote.proto` — packet schemas using custom quantized options
- `protoc-gen-bitwise` — Python plugin, reads `CodeGeneratorRequest`, emits C# serializers
- `BitWriter` / `BitReader` (C#) — bit packing + quantization (`WriteQuantizedFloat` / `ReadQuantizedFloat`), used by generated C# serializers