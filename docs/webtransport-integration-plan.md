# WebTransport as a Second Transport for Pulse — Implementation Plan

## Context

Pulse speaks **ENet/UDP only** today. Native (Unity) clients can use it, but **browsers cannot open raw UDP/ENet sockets** — the only way a browser reaches a UDP-based game server is **WebTransport** (HTTP/3 over QUIC). To let the Decentraland **web explorer** connect to Pulse, we add WebTransport as a *second, coexisting* transport **alongside** ENet — not a replacement. ENet remains the path for native clients; WebTransport unlocks the browser.

Groundwork is already on branch `feat/webtransport`: shared transport contracts were extracted into the new `src/DCLPulse.Transport.Shared` project (`PacketMode`, `DisconnectReason`, `ENetChannel`, and the ENet managed binding `ENet.cs`).

**Decisions (confirmed):**
- Primary audience: **browser clients** (drives the TLS/cert strategy below).
- Scope: deliver the **full transport end-to-end** (no separate throwaway spike — but build the FFI bridge first; see Risks).
- Rust lib delivery: a **separate repo released as a nupkg**, mirroring `Decentraland.RustEthereum`.
- Rust WebTransport lib: **`web-transport-quinn`** for the first iteration (its `Session` mirrors `quinn`'s API; `wtransport` kept as a fallback).

**Outcome:** a browser using the W3C WebTransport API completes the Decentraland ECDSA handshake and exchanges movement/emote/teleport/state traffic with Pulse. Downstream of the transport seam the server treats WT peers identically to ENet peers (same `PeerIndex`, workers, hardening, snapshot ring). Metrics and dashboards are split by transport.

---

## Architecture at a glance

Both transports are `BackgroundService`s implementing `ITransport`, feeding **one** `MessagePipe`, and sharing the `PeerIndexAllocator`, hardening, and `PeersManager` worker shards. The application layer (auth, simulation, AoI, metrics) is already transport-agnostic and does not change.

```
            ENet thread  ─┐                                  ┌─ worker[0] (peerStates, observerViews)
 browsers ─ WT thread   ─┤→ MessagePipe.incoming (multi-wr) →│  router: PeerIndex % workerCount
 natives  ─ ENet thread ─┘                                   └─ worker[N]
                         ←  MessagePipe.outgoing (per-transport drain)
```

New pieces:
1. A Rust **`web-transport-quinn` cdylib** + `Decentraland.RustWebTransport` nupkg (generic WT primitives over a synchronous poll/drain C ABI).
2. A **`WebTransportHostedService`** (mirror of `ENetHostedService`).
3. A C# **channel-semantics layer** (stream framing + datagram seq/dedup) that maps `PacketMode` onto WT streams/datagrams.
4. A **`transport` dimension** on the metrics + new WT-specific metrics.
5. **Core plumbing changes** — incoming `MessagePipe` channel relaxed to multi-writer, and the **outgoing/disconnect path re-architected** for per-transport routing behind an owner-dispatching `ITransport`. This is the most intrusive change; see §2.

Reference implementation to copy throughout: [`ENetHostedService.cs`](src/DCLPulse/Transport/ENetHostedService.cs).

---

## 1. Rust WebTransport native library

### Library choice — `web-transport-quinn` (first iteration)
Use [`web-transport-quinn`](https://docs.rs/web-transport-quinn/) (Apache-2.0/MIT, built directly on `quinn`) for the first iteration. Its `Session` API mirrors `quinn`'s `Connection` almost 1:1 — `accept_bi`/`open_bi` for reliable **streams** and `send_datagram`/`read_datagram` for unreliable **datagrams** — which is the cleanest surface to wrap in our generic C ABI, and it's the proven datagram-heavy backend behind moq-rs. The tradeoff vs the bundled-server `wtransport`: we own a little more setup — the wrapper builds the `quinn::Endpoint` + rustls `ServerConfig` (cert/key) itself and runs `web-transport-quinn`'s CONNECT upgrade on each accepted connection. Pin a version; because both libs ride `quinn`, the generic C ABI keeps **`wtransport`** as a drop-in fallback contained to the wrapper crate. **Why not stock .NET?** msquic already ships inside the .NET runtime, but neither managed surface (`System.Net.Quic` or Kestrel's experimental WebTransport) exposes unreliable **datagrams** as of .NET 10 — the Rust path is the only one that does WT streams **and** datagrams today. Full analysis in "Alternatives considered" at the end.

### New repo → nupkg (mirror `Decentraland.RustEthereum`)
Create `decentraland/rust-webtransport` producing `Decentraland.RustWebTransport.<ver>.nupkg` containing:
- `lib/net10.0/DCL.WebTransport.dll` — a **thin managed P/Invoke binding** (Host / Peer / Event / send / stats), the WT analogue of `ENet.cs`.
- `runtimes/<rid>/native/…` — the cdylib per RID: **linux-x64** (prod/Docker), **win-x64**, **osx-arm64** (dev).

### The cdylib: tokio runtime behind a synchronous poll/drain C ABI
`web-transport-quinn` (on `quinn`/tokio) is `async`; ENet's C API is **synchronous polling** (`enet_host_service`). The wrapper owns a multi-thread tokio runtime internally — building the `quinn::Endpoint` + rustls config and accepting WebTransport sessions there — and exposes a **generic, ENet-shaped** C ABI so the C# threading model is unchanged. Keep it **generic WT** — Pulse channel semantics live in C#, not Rust:

```
WtHost* wt_host_create(const WtConfig*);     // bind addr/port, cert+key PEM, max_peers, max_datagram
void    wt_host_destroy(WtHost*);
int     wt_host_service(WtHost*, uint32_t timeout_ms, WtEvent* out);  // drains 1 event; mirrors enet_host_service
        // WtEvent { type: Connect|Disconnect|StreamData|Datagram, peer_id(u64), ptr,len, remote_ip }
int     wt_peer_send_stream  (WtHost*, uint64_t peer, const uint8_t*, size_t);  // reliable byte pipe (one bidi stream/peer)
int     wt_peer_send_datagram(WtHost*, uint64_t peer, const uint8_t*, size_t);  // unreliable
int     wt_peer_disconnect   (WtHost*, uint64_t peer, uint32_t reason);
int     wt_peer_rtt_us       (WtHost*, uint64_t peer, uint64_t* out);           // for metrics parity
```
Internally: per-connection tokio tasks read stream/datagram bytes and push `WtEvent`s into an `mpsc`; `wt_host_service` pops with timeout. `peer_id` (Rust-side u64) maps to `PeerIndex` on the C# side exactly like ENet's slot id does.

### TLS / certificate strategy (browser target)
WebTransport mandates TLS over QUIC. Browsers validate two ways: a **CA-signed cert**, or **`serverCertificateHashes`** (self-signed) which forces **ECDSA-P256 + ≤14-day cert rotation**.
- **Recommended: CA-signed cert** (ACME/Let's Encrypt) on the QUIC endpoint — no rotation treadmill, works for every browser.
- Support a **self-signed-hash** mode for local dev (cert hash handed to the test client).
- Cert + key passed into `wt_host_create`; path/contents come from config (below).

### Pulse-side build wiring (replicate the RustEthereum pattern)
- [`src/Directory.Build.props`](src/Directory.Build.props): add `<RustWebTransportVersion>`.
- `tools/fetch-rust-webtransport.{sh,ps1}`: copy of [`tools/fetch-rust-eth.sh`](tools/fetch-rust-eth.sh) / `.ps1`, parameterized to the new release URL; lands the nupkg in `packages/`.
- [`src/Directory.Build.targets`](src/Directory.Build.targets): add a `FetchRustWebTransportNupkg` target (`BeforeTargets="Restore;CollectPackageReferences"`, guarded by version-set + nupkg-absent), invoking the new script.
- [`src/NuGet.config`](src/NuGet.config): already registers the local `../packages` feed — no change.
- `PackageReference Include="Decentraland.RustWebTransport" Version="$(RustWebTransportVersion)"` on the project that hosts `WebTransportHostedService` (see §4).
- **Docker** ([`src/DCLPulse/Dockerfile`](src/DCLPulse/Dockerfile), [`Dockerfile.dev-debug`](src/DCLPulse/Dockerfile.dev-debug)): add `COPY ["tools/fetch-rust-webtransport.sh", "tools/"]` before `dotnet restore`; **`EXPOSE <quic-udp-port>/udp`**. `Dockerfile.debug` uses `COPY . .` — safe. Per CLAUDE.md, **build all three images** after touching the restore pipeline.

---

## 2. Reliability model & how it affects Pulse

WebTransport offers two primitives; map `PacketMode` ([`PacketMode.cs`](src/DCLPulse.Transport.Shared/Runtime/PacketMode.cs)) onto them:

| `PacketMode` | ENet today | WebTransport mapping | Notes |
|---|---|---|---|
| `RELIABLE` | ch0 reliable ordered | **one persistent bidi stream / peer** | handshake, snapshots, emotes, teleports, resync |
| `UNRELIABLE_SEQUENCED` | ch1 (Unthrottled, stale dropped by ENet) | **datagram** + app seq header | ⚠️ see staleness gap below |
| `UNRELIABLE_UNSEQUENCED` | ch2 (Unsequenced) | **datagram** | pass-through |

### Stream framing (new)
QUIC streams are byte pipes with **no message boundaries** (ENet packets had them). The C# layer prepends a **length prefix** (u32/varint) per `ServerMessage` on send, and reassembles length-delimited `ClientMessage`s from incoming stream chunks (buffering partial reads per peer). One long-lived bidi stream per peer — **do not open a stream per message** (QUIC caps concurrent streams).

### The staleness gap (the one real reliability mismatch)
ENet's "unreliable **sequenced**" channel silently **drops stale packets at the transport**. WT datagrams are **unordered with no drop**. Confirmed in the code:
- **server→client** `PlayerStateDeltaTier0` already carries `BaselineSeq` + `NewSeq` on the wire ([`PeerViewDiff.cs`](src/DCLPulse/Peers/Diff/PeerViewDiff.cs)) → client gap detection is transport-independent. ✅
- **client→server** `PlayerStateInput` (MovementInput) carries **no** on-wire seq — it relies entirely on ENet's sequenced-drop. ⚠️

**Fix (zero protocol change):** the C# channel-semantics layer frames a datagram with a small header `{ channelId : u8, seq : u32 }` **only in the direction whose payload has no intrinsic sequence** — client→server `PlayerStateInput`. The sender assigns the next seq per `(peer, channelId)`; the receiver, on the sequenced channel, tracks the highest seq seen per `(peer, channelId)` and **drops older** ones before handing bytes up — replicating ENet's behaviour (`UNRELIABLE_UNSEQUENCED` skips the dedup; wraparound handled via RFC 1982 serial arithmetic). Server→client is deliberately **not** framed this way: every unreliable server→client message (`PlayerStateDeltaTier0`) already carries `BaselineSeq`/`NewSeq` in its body, so the server sends a **bare datagram** and the client detects staleness from that body sequence — adding a transport-level seq there would be redundant. So `DatagramSequencer` runs on the client (for `PlayerStateInput`), and `DatagramDeduper` runs on the server; the server keeps no outbound sequencer. This keeps `.proto` and all handlers untouched and matches Pulse's "channel semantics by convention" philosophy.

### Datagram size cap (~1200 B path-MTU)
ENet fragments large unreliable packets; WT datagrams cannot exceed MTU (browsers enforce strictly). Audit the largest unreliable payloads (`PlayerStateDeltaTier0`, `PlayerStateInput` — both small/quantized, expected to fit). The guard **deliberately drops and counts** an oversized unreliable message (`datagrams_dropped_oversize`) and logs an error. It must **never fall back to the reliable stream**: rerouting an unreliable message onto the ordered/reliable stream changes the channel's semantics (introducing head-of-line blocking) and masks a server-side regression — a message that outgrew the datagram budget is a bug to fix, not a case to silently absorb. **Site this guard in the WT channel-semantics layer, never as a branch inside `PeerSimulation`** — the simulation must stay transport-agnostic (CLAUDE.md `PeerSimulation` decoupling rule).

### Things that *don't* change
- **Auth handshake** is transport-agnostic (operates on `ClientMessage`). The Decentraland ECDSA chain flows over the reliable stream; `PENDING_AUTH` deadline, `HANDSHAKE_REJECT`, and `PreAuthAdmission` ([`PreAuthAdmission.cs`](src/DCLPulse/Transport/Hardening/PreAuthAdmission.cs), keys off remote IP — QUIC provides it) all work unchanged.
- **`server_tick`** comes from `ITimeProvider.MonotonicTime` (Stopwatch) in [`PeerSnapshotPublisher.cs`](src/DCLPulse/Peers/Simulation/PeerSnapshotPublisher.cs), not RTT — no dependency on transport RTT. Expose QUIC RTT only for **metrics parity**; the browser gets its own RTT from the WT/QUIC stack for animation scrubbing.
- **`PeerIndex`** ([`PeerIndexAllocator.cs`](src/DCLPulse/Peers/PeerIndexAllocator.cs)): WT gets a fresh `PeerIndex` per connection via the same allocator + pending-recycle grace period; reused-slot/reconnect caveats from CLAUDE.md apply identically.
- **Worker-shard isolation**: shared `PeerIndex` space, workers stay transport-agnostic, no cross-worker migration introduced.

### Core plumbing: outgoing routing, `ITransport`, and ordering (the genuinely intrusive change)
This is **not** "small MessagePipe changes" — it re-architects the single-transport assumptions in the send/disconnect path. Verified against the code:

- **Incoming** `Channel<IncomingEvent>` is created `SingleWriter = true` ([`MessagePipe.cs`](src/DCLPulse/Messaging/MessagePipe.cs)). Two transports = two producers → **relax to multi-writer**. Per-peer ordering still holds because **each `PeerIndex` is owned by exactly one transport for its lifetime** and that transport enqueues the peer's events in order (keep the **Connect-before-first-Receive** invariant per transport). This safety rests on the **shared `PeerIndexAllocator`** guaranteeing no two transports ever hold the same index — state that precondition explicitly; the `WorkerShard.For(From, workerCount)` router stays transport-agnostic and must not learn about transports.

- **Outgoing** `Channel<OutgoingMessage>` is **already** `SingleWriter = false` (workers/handlers post to it today). The real problem is the **single drainer**: only the ENet thread's `FlushOutgoing` reads it. A shared channel with **two** drainers is a trap — the channel is `SingleReader`, and each `TryRead` removes the item for *all* readers, so the WT loop would consume and silently drop ENet-bound messages (and vice-versa). Use **per-transport outgoing channels**: `MessagePipe.Send`/`SendDisconnect` must resolve the target peer's **owning transport at enqueue time** (a hot path — every outgoing message) and write the correct channel. This needs an **ownership registry queryable from worker threads and handlers**, not just from the transport — tag each `PeerIndex` with its transport at allocation (extend `PeerIndexAllocator`) and consult it in `Send`.

- **`ITransport` must become owner-dispatching.** Today it's a single DI singleton bound to `ENetHostedService` ([`Program.cs`](src/DCLPulse/Program.cs)), injected into `PeersManager`, `PeerSimulation` (AUTH_TIMEOUT), `HandshakeHandler` (DUPLICATE_SESSION/BANNED), and `PeerDefense`. A WT peer's `transport.Disconnect(...)` would currently land on the ENet host and **silently no-op** (its `connectedPeers` dict has no such peer). Make `ITransport` a thin facade that resolves the owning transport (same registry) and dispatches — or route all disconnects through the per-transport outgoing channels above.

- Bound the per-peer reliable-stream send queue; disconnect-on-overflow (metric).

### Capacity & admission accounting (one shared pool, two front doors)
The `PeerIndex` pool and most boards are sized from a **single** `MaxPeers` ([`Program.cs`](src/DCLPulse/Program.cs): `PeerIndexAllocator`, `SnapshotBoard`, `IdentityBoard`, `ProfileBoard`, `SpatialGrid`). Both transports draw from it:
- The WT `max_peers` passed to `wt_host_create` is **not** an independent cap — total concurrent peers across **both** transports is bounded by the shared `MaxPeers`. Reconcile them, or a WT flood exhausts the pool and ENet peers start hitting the `SERVER_FULL` refuse path. Decide the policy (shared budget vs per-transport reservations) explicitly.
- `PreAuthAdmission` (per-IP cap + global `PreAuthBudget`) and `CorruptedPacketLimiter` are now **shared across both transports** — mechanism unchanged (QUIC provides the remote IP), but *capacity semantics* change (a browser flood and an ENet flood draw the same budget). Consider whether budgets should be per-transport.
- `ACTIVE_PEERS`, `PEERS_CONNECTED`, `PEERS_DISCONNECTED` are incremented **inside** `ENetHostedService` — the WT loop must replicate every one, including the easy-to-miss `ACTIVE_PEERS.Add(-1)` on teardown, or the gauge under-counts.

---

## 3. Metrics & graphs

Existing transport metrics ([`PulseMetrics.Transport.cs`](src/DCLPulse/Metrics/PulseMetrics.Transport.cs)) are **flat counters with no labels**; the Prometheus exporter ([`PrometheusFormatter.cs`](src/DCLPulse/Metrics/PrometheusFormatter.cs)) only labels via the `WriteEnumCounters` `{type="…"}` pattern.

- **Add a `transport` dimension** (`enet` | `webtransport`) to `PEERS_CONNECTED/DISCONNECTED`, `ACTIVE_PEERS`, `BYTES/PACKETS_*`, `SEND_FAILURES`, etc. Extend `MetricsSnapshot` to carry a per-transport breakdown (or per-transport snapshots) in [`MeterListenerMetricsCollector.cs`](src/DCLPulse/Metrics/MeterListenerMetricsCollector.cs), and emit `…{transport="webtransport"}` following the existing enum-label pattern.
- **Console dashboard** ([`ConsoleDashboard.cs`](src/DCLPulse/Metrics/Console/ConsoleDashboard.cs)): split the Transport group by transport (extra column or a second section).
- **New WT-specific metrics** (use the `add-metric` skill end-to-end — instrument → accumulate → snapshot → Prometheus → dashboard). **Shipped:** `datagrams_dropped_stale`, `datagrams_dropped_oversize`. **Deferred (not yet implemented):** `active_streams`, `datagram_bytes` vs `stream_bytes`, `quic_rtt` (gauge), `tls_handshake_failures`, `send_queue_overflow_disconnects` (the per-peer send-queue bound in §2 is likewise future work), `cert_expiry_days` (gauge — operationally important; track before a CA-signed prod rollout).
- Update [`docs/metrics.md`](docs/metrics.md) and any Grafana dashboards to split panels by `transport` and add the WT panels.

---

## 4. Code layout, config, and wiring

- **`WebTransportHostedService`** in `src/DCLPulse/Transport/` next to `ENetHostedService`, implementing `ITransport` ([`ITransport.cs`](src/DCLPulse/Transport/ITransport.cs)). Its loop mirrors ENet: `wt_host_service` drain → on Connect `TryAllocate` + `PreAuthAdmission.TryAdmit` + `OnPeerConnected`; on data parse `ClientMessage` (`CorruptedPacketLimiter.RecordCorruption` on failure) + `OnDataReceived`; on Disconnect `MarkPending` + `OnPeerDisconnected` + hardening `Release`; outgoing drain maps `PacketMode` → stream/datagram.
- **Channel-semantics helper** (stream length-framing + datagram seq/dedup) in `DCLPulse.Transport.Shared` (a `WebTransport/` subfolder) so it's unit-testable in isolation. Keep `PacketMode`/`DisconnectReason` where they are; leave `ENet.cs` in place (don't refactor ENet to avoid churn).
- **Package reference**: simplest is to add the `Decentraland.RustWebTransport` `PackageReference` directly to `DCLPulse.csproj` (or a dedicated `src/DCLWebTransport` project the way `DCLAuth` holds the RustEthereum reference). Prefer adding it to the project that owns `WebTransportHostedService`.
- **Config**: a `WebTransport` section in `appsettings*.json` (`Enabled`, `Port`, `CertPath`/`KeyPath` or ACME settings, `MaxDatagramBytes`, send-queue bounds), overridable via Docker env (`WebTransport__Enabled`, …) like the existing `Peers__ResyncWithDelta`. Both transports start independently; either can be disabled.

---

## Verification

- **Rust**: `cargo test` in `rust-webtransport` — datagram echo, stream echo, accept/disconnect, oversize rejection.
- **C# unit tests** (NSubstitute, per repo convention): datagram seq/dedup incl. wraparound and reordering; stream length-framing reassembly across chunk boundaries; `PacketMode`→WT mapping; oversize-datagram guard (asserted drop + count, never a reliable-stream fallback); per-transport outgoing + `Disconnect` routing; multi-writer incoming ordering per peer; **reused-`PeerIndex` reconnect path** for WT.
- **Build**: `DOTNET_ROOT=… dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false` and `dotnet test …`. Then **build all three Docker images** (restore pipeline touched).
- **End-to-end**: `DCLPulseTestClient` speaks WebTransport via the same rust-web-transport binding (`WebTransportClient`) behind `--transport=webtransport`, reusing the MetaForge ECDSA chain — one language, the exact wire path a browser follows. Run the server in Development with WebTransport enabled, then point the client at it:
  ```
  # server — Development self-signs a dev cert and writes its SHA-256 (base64) to %TEMP%/dcl-pulse-wt-cert-hash.
  # Both transports bind 0.0.0.0 by default (see "Uniform bind" below); Metrics__Type=Prometheus keeps startup logs on stdout instead of the TUI.
  DOTNET_ENVIRONMENT=Development WebTransport__Enabled=true Metrics__Type=Prometheus dotnet run --project src/DCLPulse
  # client — reads that hash file to pin the dev cert, then connects.
  dotnet run --project src/DCLPulseTestClient -- --transport=webtransport --port=7443
  ```
  Verified server-side: `WebTransport peer connected …`, `Peer handshake accepted with wallet 0x…`, the teleport, and the profile announcement — the DCL handshake and reliable-stream traffic complete over WebTransport. Prometheus then emits `transport="webtransport"`; an ENet `DCLPulseTestClient` can run in parallel to confirm both coexist.
- **Uniform bind**: ENet and WebTransport share one binding shape — `Transport:BindHost`/`Transport:Port` and `WebTransport:BindHost`/`WebTransport:Port` — both defaulting to the IPv4 wildcard `0.0.0.0`, so a `127.0.0.1` client reaches either transport identically on Windows and Linux. Set `BindHost` to `::` for the IPv6 wildcard: ENet enables dual-stack so `::` also accepts IPv4 on any OS, while WebTransport's `::` is dual-stack on Linux but IPv6-only on Windows (there a `127.0.0.1` client would need `--ip=::1`).

---

## Risks & sequencing

- **Build the FFI tokio↔poll bridge first** with a Rust echo test, even without a formal spike — it's the highest-uncertainty piece and everything depends on it.
- `web-transport-quinn` maturity — pin a version; the generic C ABI keeps `wtransport` as a drop-in fallback (both ride `quinn`, so a swap is contained to the wrapper crate).
- **Cert ops** — go CA-signed to avoid the 14-day `serverCertificateHashes` rotation; track `cert_expiry_days`.
- **Browser datagram size** is strict — validate the size guard early against a real browser.
- **Outgoing/disconnect routing (per-transport channels + owner-dispatching `ITransport`) is the genuinely under-scoped core change** — not "small MessagePipe edits." It touches `Send` on the hot path and every `ITransport.Disconnect` call site. Land it behind focused tests (owner routing, multi-writer incoming ordering, no cross-transport message theft) before layering the WT transport on top. Reconcile the shared `MaxPeers` / `PreAuthBudget` accounting at the same time.

---

## Alternatives considered: .NET-native WebTransport (deferred — datagram gap)

Before committing to a Rust native lib we evaluated implementing WebTransport with **stock .NET**, since msquic already ships inside the .NET runtime (`System.Net.Quic` is a managed wrapper over it). **Verdict: not viable today — the blocker is unreliable datagrams**, which Pulse's movement/state channel requires.

| Candidate | Streams | Datagrams | Blocker |
|---|---|---|---|
| **Kestrel WebTransport** (ASP.NET Core, experimental) | ✅ `IHttpWebTransportFeature` → `AcceptAsync` → `AcceptStreamAsync` | ❌ "fakes supporting datagrams by ignoring them when received" | Preview-gated (opt-in switch + `EnablePreviewFeatures`); implements **draft-02** only (browsers track ≈draft-15 → interop risk); datagram issue [aspnetcore#42784](https://github.com/dotnet/aspnetcore/issues/42784) is Backlog, no PR, no target version |
| **System.Net.Quic** (raw QUIC = the bundled msquic) | ✅ `QuicListener`/`QuicConnection`/`QuicStream` | ❌ no public datagram API | [runtime#53533](https://github.com/dotnet/runtime/issues/53533) open since 2021, unshipped ([#123418](https://github.com/dotnet/runtime/issues/123418) closed as its dup). Also you'd hand-write the entire HTTP/3 + WebTransport session layer (extended CONNECT, capsules) |
| **msquic via direct P/Invoke** | ✅ | ✅ (msquic has them) | Still native interop, **and** you implement all of H3 + WT yourself — more work than the Rust wrapper, less payoff |
| **HttpClient + HTTP/3** | n/a | n/a | Client-side only; plain HTTP/3 ≠ WebTransport |

Reliable-streams-only is **not** an acceptable fallback for the hot channel: it reintroduces head-of-line blocking and stale-packet retransmission (CLAUDE.md: *"a retransmitted stale position is worse than a skipped one"*).

**Architecture fit if/when the managed path becomes viable.** Pulse runs a **plain Generic Host** (`Host.CreateApplicationBuilder`, SDK `Microsoft.NET.Sdk.Worker`) with **no ASP.NET Core** — the metrics endpoint is a raw `HttpListener` in [`HttpService.cs`](src/DCLPulse/HttpService.cs), and the prod image is `mcr.microsoft.com/dotnet/runtime:10.0`. Going managed would mean: add the `Microsoft.AspNetCore.App` framework reference, switch the prod base image to `aspnet:10.0`, and install `libmsquic` in Docker (it is **not** in the base images). All one-time and modest. The WT transport would be a new `BackgroundService` bridging Kestrel WebTransport sessions into `MessagePipe`, coexisting with ENet exactly like `HttpService` does today.

**Decision.** Adopt the Rust `web-transport-quinn` lib now (only full-WT option), and **keep the WT implementation behind the swappable seam** (C# channel-semantics layer; only the low-level primitives sit behind the binding). **Track [runtime#53533](https://github.com/dotnet/runtime/issues/53533) + [aspnetcore#42784](https://github.com/dotnet/aspnetcore/issues/42784)** — the day both ship, migrating to a zero-FFI managed stack (Kestrel WebTransport, or raw `System.Net.Quic` + a thin session layer, over the msquic already in the runtime) becomes the obvious end-state and deletes the entire Rust workstream.
```
