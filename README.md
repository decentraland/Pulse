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

## Decentraland.RustEthereum native dependency

ECDSA signature verification on the auth chain is delegated to the
[`decentraland/rust-ethereum`](https://github.com/decentraland/rust-ethereum) NuGet
package. The package is **not** on a public feed — it ships as a GitHub Release
asset and is fetched from there on demand.

### Pinning the version

The version is the **single source of truth** in `src/Directory.Build.props`:

```xml
<RustEthereumVersion>0.2.0-rc2</RustEthereumVersion>
```

Both consuming projects (`DCLAuth`, `Fuzzer`) reference it via
`$(RustEthereumVersion)`. To bump, edit that one value.

### Fetching the .nupkg

`src/NuGet.config` adds the repo-root `packages/` directory as a local NuGet
source. The directory is gitignored — populated automatically on every
`dotnet restore` via `src/Directory.Build.targets`, which runs
`tools/fetch-rust-eth.{sh,ps1}` before `Restore` if the matching
`Decentraland.RustEthereum.<version>.nupkg` is missing.

Bump `RustEthereumVersion` and the next restore pulls the new nupkg from the
GitHub Release. The script is idempotent and concurrency-safe (temp file +
atomic rename), so the parallel restore graph triggering it from every project
is fine.

To pre-fetch outside of a restore (e.g. air-gapped builds), invoke the script
directly:

```bash
bash tools/fetch-rust-eth.sh        # Linux / macOS / git-bash
pwsh tools/fetch-rust-eth.ps1       # Windows PowerShell
```

## ENet native library

The server's ENet library (`enet.dll`, `libenet.so`, `libenet.dylib`) is built from the Rust glue
crate in `native/enet/`. The glue wraps `rusty_enet` from
[`decentraland/dcl-one`](https://github.com/decentraland/dcl-one), the ENet the Rust clients use, so
the server and those clients run one ENet implementation. The glue exports 31 of the 65 `enet_*`
functions `src/DCLPulse.Transport/Package/Runtime/ENet.cs` imports; calling any other throws
`EntryPointNotFoundException`. `ENet.cs` is unchanged. The Linux library needs glibc
2.34 or newer. See the [design](docs/superpowers/specs/2026-11-25-rust-enet-drop-in-design.md) for
details.

### Differences from ENet-CSharp

- The server takes the smaller of the two MTUs and reads datagrams up to 4096 bytes.
- A timed-out peer arrives as `Disconnect`, not `Timeout`.
- The keepalive ping goes out every 500 ms.
- A listening host refuses a packet declared above 8192 bytes before reassembly and disconnects that
  peer with `PACKET_CORRUPTED`, so raising `Transport:BufferSize` above 4096 no longer raises the hard
  limit.
- A send error costs one datagram and does not fail the service call.

The design's "Behaviour that changes" table has the full list.

### Pin

`native/enet/PIN.json` names the dcl-one commit, the folder `library/vendor/rusty_enet` and its Git
tree SHA. The source is never committed. The toolchain is pinned by `native/enet/rust-toolchain.toml`.

### Building locally

```bash
python scripts/enet-fetch-source.py fetch    # downloads the pinned folder into native/enet/vendor/ and verifies it
cd native/enet
cargo build --release                        # enet.dll / libenet.so / libenet.dylib
cargo test
```

`python scripts/enet-fetch-source.py verify` re-checks the fetched source without network access.

### Committed libraries

The libraries in `src/DCLPulse/runtimes/{win-x64,linux-x64,macos-arm64}/native/` are built by
`.github/workflows/enet-native.yml` (pull requests that touch `native/enet/**`, the fetch script or
the workflow, and manual dispatch).
It uploads the artifacts `enet-win-x64`, `enet-linux-x64` and `enet-macos-arm64`, each with a `.sha256`.
`native/enet/PROVENANCE.json` records the pin, the toolchain, a digest of the glue sources and the
SHA-256 of each library. `python scripts/enet-provenance.py check` verifies it and runs in
`.github/workflows/test.yml`.

`.github/workflows/enet-pin.yml` runs daily, on manual dispatch and on pull requests. It checks that
the pinned `rev` is a commit on dcl-one's `main` and compares the pinned tree SHA with the one on
`main`. A finding, or a failed GitHub API lookup, fails the scheduled and manual runs and only warns
on pull requests.

### Moving the ENet pin

1. Edit `rev` and `tree_sha` in `native/enet/PIN.json`.
2. Open a pull request so `enet-native.yml` builds the libraries.
3. Take the three artifacts from the `enet-native` run on the pull request's head commit in this
   repository. Check each file against its `.sha256` and against the hash printed in that job's log.
   Copy `enet.dll` to `src/DCLPulse/runtimes/win-x64/native/`, `libenet.so` to
   `src/DCLPulse/runtimes/linux-x64/native/` and `libenet.dylib` to
   `src/DCLPulse/runtimes/macos-arm64/native/`. Never commit a library built on a developer machine.
4. Run `git add native/enet src/DCLPulse/runtimes`, then `python scripts/enet-provenance.py write`
   and `python scripts/enet-provenance.py check`. `write` digests the git index, so an unstaged
   `PIN.json` or `Cargo.lock` gives a stale digest.
5. Run the .NET tests and the end-to-end validation with `--enet=csharp` and with `--enet=rust`.
6. Commit.

## Transport

Pulse uses ENet over UDP and WebTransport with a shared peer allocator. `PeerIndex` is a
recycled server slot; use identity registrations to fence connection lifetimes. See
[architecture](docs/ai-agent-context.md) for transport, lifecycle and synchronization rules.

### Capacity tuning

Two knobs control concurrent-peer capacity:

- `Transport.MaxPeers` — size of the `PeerIndex` pool and every per-peer array board (`SnapshotBoard`, `IdentityBoard`, `ProfileBoard`, `RealmSpatialGrids`). Hard ceiling on active + in-grace slots.
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
| `--enet=<csharp\|rust>` | `csharp` | ENet library the client loads: ENet-CSharp (as Unity) or dcl-one's rusty_enet (as Bevy) |

Example — single bot connecting to a remote server:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=bot1 --ip=10.0.0.5 --pos-x=0 --pos-z=0
```

Example — 10 bots for load testing:

```bash
dotnet run --project src/DCLPulseTestClient -- --account=loadtest --bot-count=10 --ip=10.0.0.5
```

`--enet=csharp` loads ENet-CSharp from `enet-csharp/` in the output folder, the library the Unity client uses (announced MTU 1280). `--enet=rust` loads the server's library from `enet-rust/`, the same ENet the Bevy client uses (announced MTU 1392). The client prints `ENet library: <choice>` on start and exits with code 2 on an unknown value. The option only applies to the ENet transport.

When `--bot-count=1`, the account name is used as-is. When `--bot-count` > 1, accounts are named `<account>-0`, `<account>-1`, ..., `<account>-N-1` and bots spawn in a circle around the initial position.

### End-to-end conn-string harness

With `--comms-enabled` each bot also opens a ws-connector session on its own wallet, so one process holds both channels and can assert that a LiveKit connection string arrives for the same identity that Pulse clustered. `docker-compose.e2e.yml` brings up the NATS + ws-connector + Pulse stack it runs against; comms-gatekeeper mints the conn strings and is run separately, since it needs Postgres and LiveKit credentials.

The test client stays a client — it holds no broker connection and never publishes on a service's subject.

`--comms-url` accepts either a plain `ws://host/ws` or a realm's raw comms adapter string (`archipelago:archipelago:wss://host/ws`), so a value copied out of `/about` works unchanged.

See [docs/e2e-livekit.md](docs/e2e-livekit.md) for prerequisites, the bridge modes, and how to read a failure.

See [cluster assignment recovery](docs/cluster-assignment-recovery.md) for the NATS lookup,
retained pending takeovers, token floors, recovery limits, and broker-test setup.

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

## Deployments

`pulse-server` is the instance the client talks to. It ships automatically from `main` (**CI/CD on main branch**) and can be pushed to an environment by hand with **Manual Deploy**.

`pulse-archipelago` is a second instance in `.zone`, for hands-on Archipelago testing. It is entirely manual in both directions, through the **Manual Deploy (pulse-archipelago)** action:

| Action | What it does |
| --- | --- |
| `up` | Deploys `quay.io/decentraland/pulse-server:<tag>` and runs one task |
| `down` | Deploys the `parked` sentinel tag, which drops the service to zero tasks |

Two things to know before relying on it:

- **`down` is not a `pulumi destroy`.** The stack, the target group and the Cloudflare record survive — only the Fargate task goes away. Since the NLB and the ALB are shared across services, a parked instance costs essentially nothing. A real destroy means running `ops/services-pipeline` in GitLab with `PULUMI_ACTION=destroy`.
- **The `parked` string is coupled across two repos.** It lives in this repo's workflow and in `src/services/pulse-archipelago.ts` in `decentraland/definitions`. Changing one without the other silently breaks the switch. The tag carries the switch because the GitHub deploy path forwards a fixed set of pipeline variables, and `SERVICE_IMAGE` is the only one a workflow controls freely.

Two differences from `pulse-server` worth knowing when you point a client at it:

- **It answers on UDP `7778`, not `7777`** — `pulse-archipelago.decentraland.zone:7778`. The NLB is shared per environment and its listeners are L4, one per port, so `pulse-server` already owns 7777 there. The definition sets `Transport__Port` to match, since the NLB maps its port straight through to the container.
- **No WebTransport.** The exportable WT certificate is issued for `pulse-server.<domain>` only, so this instance is UDP/ENet only.

## Debugging

Local debugging uses `docker-compose.debug.yml` with Rider attaching via the Docker socket. Remote debugging against the dev environment uses `Dockerfile.dev-debug` (Debug build + vsdbg + sshd + pre-installed JetBrains RiderRemoteDebugger) deployed via the **Deploy Dev (Debug)** GitHub Action, with Rider attaching over SSH through the bastion.

See [docs/debugging.md](docs/debugging.md) for full setup and workflows. Bastion/tunnel specifics are in the `decentraland/playbooks` repo (internal access only).

## Troubleshooting

### Port 5000 already in use (macOS)

The HTTP service listens on **port 5000** by default and the port must be free for the server to start. On macOS, port 5000 is grabbed by **AirPlay Receiver** out of the box — disable it under *System Settings → General → AirDrop & Handoff → AirPlay Receiver*, or override the port via `HttpService:Port` in `appsettings.json`.
