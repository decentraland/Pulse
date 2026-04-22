# Pulse
.NET-based authoritative server for fulminant social interactions

## Protocol generation

Protocol C# files are auto-generated from the sibling [protocol](https://github.com/decentraland/protocol/tree/quantization) repo using `protoc` + the `protoc-gen-bitwise` plugin.

### Prerequisites

The `protocol` repo must be checked out as a sibling of this repo:

```
D:\<root>\protocol\   ← @dcl/protocol (quantization branch)
D:\<root>\Pulse\      ← this repo
```

You can override the path via `Directory.Build.props` or `-p:_ProtocolRepo=<path>`.

### GenerateProto switch

The `Protocol.csproj` has a `GenerateProto` property that controls whether `.proto` files are regenerated at build time or the committed `Generated/` files are used as-is.

| Mode | When to use | What happens |
|------|------------|--------------|
| `GenerateProto=true` (default) | Local development with the `protocol` repo available | Runs `protoc` + bitwise plugin, regenerates `src/Protocol/Generated/` |
| `GenerateProto=false` | Docker builds, CI, or when the `protocol` repo is not available | Skips generation, compiles committed `Generated/*.cs` files directly |

To build without generation:

```bash
dotnet build -p:GenerateProto=false
```

After modifying `.proto` files, build normally (or explicitly with `GenerateProto=true`) and commit the updated `Generated/` files so Docker and CI builds stay in sync.

#### Rider

To set `GenerateProto` from Rider:

- **Solution-wide:** Settings → Build, Execution, Deployment → Toolset and Build → MSBuild CLI arguments → add `-p:GenerateProto=false`
- **Per configuration:** Run → Edit Configurations → select configuration → Before launch → click the build step → add `-p:GenerateProto=false` to MSBuild arguments

In practice the default (`true`) is correct for local development. The `false` value is used by Docker/CI builds that don't have the protocol repo available.

## Transport

Pulse uses ENet over UDP. A couple of non-obvious behaviors worth knowing before reading or changing server code:

- **`PeerIndex` is a recycled slot, not an identity.** It wraps `ENetPeer.ID`, which ENet reassigns to the next connecting peer as soon as the previous one is freed. The stable identity is the wallet address from the auth handshake — resolve it through `IdentityBoard` rather than treating `PeerIndex` as the player.
- **Any per-observer state keyed by `PeerIndex` must be invalidated on disconnect.** If a view, cache, or baseline keyed by `PeerIndex` outlives the peer, the next peer that lands on that slot will silently inherit the stale state — no `PlayerJoined` for the new player, wrong wallet cached on clients, deltas diffed against the old baseline.
- **Channel convention is enforced by packet flags, not by ENet.** Ch0 is reliable control flow (snapshots, events, resyncs); ch1 is unreliable sequenced (high-frequency state updates, input).

### Capacity tuning

Two knobs control concurrent-peer capacity:

- `Transport.MaxPeers` — size of the `PeerIndex` pool and every per-peer array board (`SnapshotBoard`, `IdentityBoard`, `ProfileBoard`, `SpatialGrid`). Hard ceiling on active + in-grace slots.
- `Transport.MaxConcurrentConnections` — ENet host capacity. `0` = `MaxPeers`. Set below `MaxPeers` to reserve slots for the allocator's pending-recycle grace window — without headroom, a burst of reconnects can exhaust the `PeerIndex` pool while ENet still has free slots, causing `SERVER_FULL` refusals on otherwise admittable connections.

Rule of thumb: `MaxConcurrentConnections ≈ MaxPeers - ceil(peakDisconnectsPerSecond × Peers.DisconnectionCleanTimeoutMs / 1000)`.

## Metrics & Dashboard

Pulse includes a real-time terminal dashboard for monitoring transport throughput, queue backpressure, and per-message-type rates during development. Enable it with `"Dashboard": { "Enabled": true }` in `appsettings.json`.

See [docs/metrics.md](docs/metrics.md) for a full reference of all tracked metrics, how to interpret them, and how to add new ones.

## DCLPulseTestClient

A headless test client that connects to the Pulse server as a bot player. Uses ENet over UDP with the same protocol as the Unity client. Supports running multiple bots from a single process for load testing. Useful for load testing, debugging server behavior, and verifying the protocol without launching the full Explorer.

### Prerequisites

- [MetaForge](https://github.com/decentraland/MetaForge) CLI installed and available on `PATH` (the client shells out to `metaforge` for account creation, auth chain signing, and profile fetching)
- A running Pulse game server

### Running

```bash
dotnet run --project src/DCLPulseTestClient
```

#### CLI arguments

| Argument | Default | Description |
|---|---|---|
| `--account=<name>` | `enetclient-test` | MetaForge account name (or prefix when using multiple bots) |
| `--bot-count=<N>` | `1` | Number of bots to spawn in the same process |
| `--ip=<address>` | `127.0.0.1` | Server IP address |
| `--port=<port>` | `7777` | Server UDP port |
| `--pos-x=<float>` | `-104` | Spawn position X (Genesis Plaza) |
| `--pos-y=<float>` | `0` | Spawn position Y |
| `--pos-z=<float>` | `5` | Spawn position Z |
| `--spawn-radius=<float>` | `10` | Radius of the circle bots spawn on around the initial position |
| `--dispersion-radius=<float>` | `20` | Max distance a bot can wander from the spawn origin |
| `--rotate-speed=<deg/s>` | `90` | Idle rotation speed in degrees per second |

Example — single bot connecting to a remote server:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=bot1 --ip=10.0.0.5 --pos-x=0 --pos-z=0
```

Example — 10 bots for load testing:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=loadtest --bot-count=10 --ip=10.0.0.5
```

When `--bot-count=1`, the account name is used as-is. When `--bot-count` > 1, accounts are named `<account>-0`, `<account>-1`, ..., `<account>-N-1` and bots spawn in a circle around the initial position.

### What the bot does

On startup each bot authenticates via MetaForge, connects over ENet, completes the handshake, announces its profile, then enters a 30 fps simulation loop.

**Autonomous behavior (default):**

- **Wanders** using three Perlin noise generators (forward, strafe, rotation) producing smooth, organic movement at 5 units/s
- **Plays a random emote** from the profile's emote list every 5 seconds, then stands still for a 5-second cooldown before resuming movement
- **Handles resync** — detects sequence gaps in incoming state deltas and sends `RESYNC_REQUEST` to the server
- Sends `PlayerStateInput` on the unreliable sequenced channel every tick with position, velocity, rotation, and state flags (`Grounded`)

**Keyboard override** (single-bot mode only) — the bot accepts keyboard input in parallel:

| Key | Action |
|---|---|
| W / A / S / D | Move forward / left / backward / right |
| Q / E | Rotate left / right |
| B then 0–9 | Play emote by index |
| ESC | Quit |

In multi-bot mode keyboard input is disabled; use Ctrl+C to stop all bots.

### Multi-bot architecture

All bots share a single ENet `Host` (one UDP socket, one service thread) with N peers. Each bot gets its own `MessagePipe` and `PulseMultiplayerService` for isolated message routing. The ENet transport multiplexes outgoing messages from all bots and routes incoming packets by peer ID.

```
ENetTransport (shared)              One Host, one thread, N peers
├── BotSession 0                    Per-bot state + pipe + service
│   ├── BotTransport                ITransport adapter → shared ENetTransport
│   ├── MessagePipe                 Isolated incoming/outgoing channels
│   ├── PulseMultiplayerService     Handshake, subscriptions
│   └── Bot                         Perlin-noise input generator
├── BotSession 1
│   └── ...
└── BotSession N-1
    └── ...
```

### Architecture

```
Program.cs                          Entry point, N-bot orchestration, shared game loop
BotSession.cs                       Per-bot state (position, rotation, seq tracking)
├── Auth/
│   ├── MetaForgeAuthenticator      Shells out to `metaforge account create/chain`
│   └── IAuthenticator              Interface for swappable auth strategies
├── Profiles/
│   ├── MetaForgeProfileGateway     Shells out to `metaforge account info`
│   └── IProfileGateway             Interface for swappable profile sources
├── Inputs/
│   ├── Bot                         Perlin-noise wandering + periodic emotes
│   ├── ConsoleInputReader          WASD + emote keyboard input (single-bot only)
│   ├── BotWithManualExitInput      Composite: keyboard (ESC check) → Bot
│   └── PlayLoopEmote               One-shot: fires a single looping emote
├── Networking/
│   ├── ENetTransport               Shared ENet Host, multi-peer, single thread
│   ├── BotTransport                Per-bot ITransport adapter
│   ├── MessagePipe                 Thread-safe channel bridging transport ↔ game thread
│   └── PulseMultiplayerService     Handshake, message routing, typed subscriptions
└── ParcelEncoder                   Global ↔ parcel-relative position conversion
```

## Debugging

Local debugging uses `docker-compose.debug.yml` with Rider attaching via the Docker socket. Remote debugging against the dev environment uses `Dockerfile.dev-debug` (Debug build + vsdbg + sshd + pre-installed JetBrains RiderRemoteDebugger) deployed via the **Deploy Dev (Debug)** GitHub Action, with Rider attaching over SSH through the bastion.

See [docs/debugging.md](docs/debugging.md) for full setup and workflows. Bastion/tunnel specifics are in the `decentraland/playbooks` repo (internal access only).