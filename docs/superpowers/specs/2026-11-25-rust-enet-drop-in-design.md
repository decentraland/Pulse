# Design: run the Pulse ENet server on dcl-one's rusty_enet

- Date: 2026-11-25
- Status: approved 2026-11-25

## Intent

.NET Pulse serves ENet through prebuilt ENet-CSharp 2.4.9 native libraries. dcl-one's Rust Pulse
server and the Bevy Explorer (headless included) use a different implementation: the vendored
`rusty_enet` tree in dcl-one. The two implementations already disagree in a way that loses data.
ENet-CSharp grants a client's announced MTU but reads only 1280-byte datagrams, so a client that
announces 1392 has every larger datagram dropped with no trace.

The goal is one ENet implementation on both sides, so they cannot drift apart again. Pulse replaces
its ENet-CSharp native libraries with libraries compiled from dcl-one's `rusty_enet`, used as a
drop-in: same file names, same exported function names, no change to dcl-one.

## Goals

- The Pulse server's ENet runs on dcl-one's `library/vendor/rusty_enet`, byte for byte, at a pinned
  dcl-one commit.
- dcl-one needs no change, and no copy of its ENet source lives in the Pulse repository.
- `DCLPulseTestClient` can run on either library, chosen at start: ENet-CSharp, which is what
  Unity uses, or the new one, which is what Bevy uses.
- `ENet.cs`, `ENetChannel.cs`, `ENetHostedService` and the test client's `ENetTransport` keep
  working without source changes. The test client gains one option and a few lines at startup.
- A client that announces any MTU up to 4096 gets its datagrams delivered.
- CI flags when dcl-one's ENet tree moves ahead of the pin.

## Non-goals

- Changing Unity. It keeps ENet-CSharp and the native libraries it ships itself.
- Adopting dcl-one's server-side receive limits (`ReceivePolicy`) or its socket-error counters.
  ENet-CSharp's interface has no place for them. They are a possible follow-up.
- Implementing all 68 functions `ENet.cs` imports. Only the ones listed below are in scope.
- Building the native libraries inside the Docker images or during `dotnet build`.

## Anti-goals

- A patched or forked copy of `rusty_enet`. If the glue needs something the crate does not expose,
  the design changes; the crate does not.
- A silent behaviour change. Every difference from ENet-CSharp that the server can observe is
  listed in this document and covered by a test or accepted here.
- A native crash or a panic that unwinds into .NET.

## Constraints

- .NET cannot call `rusty_enet` directly. The crate has no C interface (`extern "C"`, `no_mangle`
  and `cdylib` do not appear in it), and its types are generic over a `Socket` trait. A Rust glue
  crate is unavoidable; it lives in the Pulse repository.
- The glue may use only `rusty_enet`'s public API. In particular `Packet`'s inner pointer is
  `pub(crate)`.
- The source is dcl-one (public) at `library/vendor/rusty_enet`: rusty_enet 0.4.0, upstream
  `robtfm/rusty_enet@6fa651d`, plus dcl-one patches 0001 to 0007. Patch 0001 adds
  `Packet::new_unthrottled`, which Pulse needs for `PacketFlags.Unthrottled` on channel 1.
- ENet is driven from one dedicated thread per host. The glue keeps that rule and adds no locking.
- `ENet.cs` belongs to the `com.decentraland.pulse.transport` Unity package. It must not change in
  a way Unity would notice; this design does not change it at all.
- `Library.Initialize` throws unless `enet_linked_version()` returns `(2 << 16) | (4 << 8) | 9`.
- Commits in this repository are GPG-signed, so a workflow cannot commit the built libraries by
  itself.
- Pulse hot paths stay allocation-free on the managed side. This holds because the managed side
  does not change.

## Approaches

1. **New repository with a small purpose-built interface**, shipped as a NuGet package like
   `rust-web-transport`. One native call and one copy per packet, and room for receive limits and
   drop counters. It rewrites `ENetHostedService` and adds a repository to keep in step.
2. **Drop-in replacement for the native library, glue crate in the Pulse repository** (chosen).
   The glue exports ENet-CSharp's function names over `rusty_enet`. No C# changes and no dcl-one
   changes. It keeps today's call count per packet, and the Rust side must imitate ENet-CSharp's
   handles and packet ownership.
3. **The same glue inside dcl-one**, next to the vendored tree. No pin to keep in step, but a Pulse
   dependency would be released from the monorepo's CI and review process.

The user chose approach 2: the point is to stop drift, with dcl-one untouched and all the work in
Pulse.

## Design

### Layout

```
native/enet/                    Rust glue crate (cdylib), own Cargo workspace
  PIN.json                      dcl-one repository, commit, path and Git tree SHA of rusty_enet
  vendor/                       git-ignored; filled by scripts/enet-fetch-source.py
  Cargo.toml                    rusty_enet = { path = "vendor/dcl-one/library/vendor/rusty_enet" }
  Cargo.lock                    committed
  rust-toolchain.toml           pinned toolchain
  src/                          exported enet_* functions, socket, handle tables
  tests/                        Rust integration tests
  PROVENANCE.json               pin, toolchain, source digest, SHA-256 of each committed binary
src/DCLPulse/runtimes/          enet.dll, libenet.so, libenet.dylib: replaced by the glue build
src/DCLPulseTestClient/runtimes/  unchanged: the ENet-CSharp libraries, for the Unity mode
.github/workflows/              enet-native.yml (build), enet-pin.yml (drift check)
```

### Source pin

`native/enet/PIN.json` names the repository (`https://github.com/decentraland/dcl-one`), an exact
commit `rev`, the path `library/vendor/rusty_enet`, and that folder's Git tree SHA.
`scripts/enet-fetch-source.py` fetches only that folder at that commit into the git-ignored
`native/enet/vendor/` (a sparse, blob-filtered Git fetch) and fails unless the fetched folder's
tree SHA equals the pinned one. A Git tree SHA is content-addressed, so the bytes are dcl-one's by
construction. `native/enet/Cargo.toml` depends on the crate by path into `vendor/`.

A plain Cargo git dependency was tried first and dropped on 2026-11-25: Cargo clones and checks
out the whole monorepo, which took 45 minutes and then failed on Windows' path-length limit in
files unrelated to ENet.

The pin is dcl-one's main at the time of the build, starting at
`43dfb8e3e14c599972d3cca0ad4c718d9052b4ad` (2026-11-25). dcl-one's Pulse server and its Bevy
Explorer both build this one path. A deployed client built from an older dcl-one commit may carry
an older tree; that is outside this change, and the pin follows main.

The source is never committed to Pulse.

### Exported functions

The glue exports exactly these, with ENet-CSharp's C signatures and `cdecl`:

| Group | Functions |
| --- | --- |
| Library | `enet_initialize`, `enet_deinitialize`, `enet_linked_version`, `enet_time_get` |
| Address | `enet_address_set_ip`, `enet_address_set_hostname`, `enet_address_get_ip` |
| Host | `enet_host_create`, `enet_host_destroy`, `enet_host_service`, `enet_host_check_events`, `enet_host_flush`, `enet_host_connect` |
| Peer | `enet_peer_get_id`, `enet_peer_get_ip`, `enet_peer_get_port`, `enet_peer_get_mtu`, `enet_peer_get_state`, `enet_peer_get_rtt`, `enet_peer_timeout`, `enet_peer_ping_interval`, `enet_peer_send`, `enet_peer_disconnect`, `enet_peer_disconnect_now`, `enet_peer_disconnect_later`, `enet_peer_reset` |
| Packet | `enet_packet_create`, `enet_packet_create_offset`, `enet_packet_get_data`, `enet_packet_get_length`, `enet_packet_dispose` |

This covers every call `ENetHostedService`, its hardening partial,
`ENetHostedServiceShutdownTests` and the test client's `ENetTransport` make. That includes the
client side: a host created with a null address, `enet_host_connect`, and
`enet_address_set_hostname`, which resolves a DNS name the way ENet-CSharp does (first IPv4 or
IPv6 result, IPv4 stored as mapped). The rule for the builder: every native call reachable from
the server, the test client and the tests is exported. Anything else `ENet.cs` imports is not
exported. Calling
one raises `EntryPointNotFoundException` in .NET, which is the intended loud failure.

`enet_linked_version` returns 2.4.9 so `Library.Initialize` accepts the library.
`enet_initialize` and `enet_deinitialize` return success and do nothing.

### Handles

- **Host:** a pointer to a heap-allocated glue host. It owns the `rusty_enet::Host`, the socket,
  the peer records, and the one pending event.
- **Peer:** a pointer to a peer record `{ host, index }`. Records are allocated once per host, one
  per slot, and live until `enet_host_destroy`. A peer handle therefore stays valid memory after
  `DisconnectNow`, which matters because `ENetHostedService` keeps `Peer` structs in
  `connectedPeers`. `enet_peer_get_id` returns the slot index, the same value ENet-CSharp returns.
- **Packet:** a pointer to a boxed `rusty_enet::Packet`. `enet_packet_create` copies the caller's
  bytes into it once and applies the flags. A received packet is boxed and handed out in the
  event.

Packet ownership follows ENet-CSharp as the server uses it:

- `enet_peer_send` returning 0 consumes the handle. The caller must not use or dispose it after.
- `enet_peer_send` returning a negative value leaves the handle with the caller.
- `enet_packet_dispose` frees a handle the caller still owns.

One difference: in ENet-CSharp, disposing a packet after a successful send is a harmless no-op. In
the glue it is a double free. No server or test code does this today; the builder greps for it and
the doc comment on the export states the rule.

### Flags

| `PacketFlags` | Glue mapping |
| --- | --- |
| `Reliable` | `PacketKind::Reliable` |
| none | `PacketKind::Unreliable { sequenced: true }` |
| `Unsequenced` | `PacketKind::Unreliable { sequenced: false }` |
| `UnreliableFragmented` | `PacketKind::AlwaysUnreliable`, sequencing as above |
| `Unthrottled` | `Packet::new_unthrottled` with the kind above |
| `Instant` | `Host::flush` after a successful send |
| `NoAllocate` | ignored; the glue always copies |

Channel 1 (`PacketFlags.Unthrottled`, sequenced unreliable) and channel 2 (`Unsequenced`) keep
their meaning. An unreliable packet too large for one datagram falls back to reliable fragments in
both libraries.

### Socket

The glue implements `rusty_enet::Socket` over one non-blocking UDP socket.

- `enet_host_create` with an address binds it; with a null address it binds an ephemeral port.
- ENet-CSharp stores every address as IPv6 and maps IPv4 into it. The glue binds an IPv4-mapped
  address (`0.0.0.0`, the Pulse default) as IPv4, and a real IPv6 address (`::`) as a dual-stack
  IPv6 socket with `IPV6_V6ONLY` off, so `::` still accepts IPv4 on Windows and Linux.
- `enet_peer_get_ip` prints an IPv4 or IPv4-mapped address in dotted form and anything else as
  IPv6 text, like `inet_ntop`. `IpLimiter`, the whitelist and `ContinentResolver` key on this
  string, so it must match ENet-CSharp's output.
- Receive reads into `rusty_enet`'s full `MTU_MAX` (4096-byte) buffer.
- A receive error that concerns one datagram (an ICMP answer to an earlier send, or on Windows a
  datagram larger than the buffer) is skipped. Any other receive error makes `enet_host_service`
  return a negative value.
- A send error costs that one datagram and is never returned to `rusty_enet`: the socket reports
  0 bytes sent, and ENet resends what was reliable. This is what dcl-one's Pulse driver does
  (`services/catalyst/comms/src/pulse/transport/host.rs`). Otherwise one unreachable peer address
  would abort every service pass and starve the other peers. A socket that is gone fails the next
  receive, which reports the error.
- Send and receive buffer sizes come from the `SocketOptions` that `rusty_enet` passes to `init`.

### Service loop

`enet_host_service(host, event, timeout)` keeps ENet's contract: return 1 with one event, 0 on
timeout, negative on error. `rusty_enet`'s `Host::service` never blocks, so the glue calls it and,
when it yields no event, waits for the socket to become readable until the deadline, then calls it
again. `enet_host_check_events` maps to `Host::check_events`.

Events map to ENet-CSharp's `ENetEvent` layout (`type`, `peer`, `channelID`, `data`, `packet`):

| rusty_enet event | `ENetEvent.type` |
| --- | --- |
| `Connect` | 1, with `data` |
| `Disconnect` | 2, with `data` |
| `Receive` | 3, with channel and a packet handle |
| `ReceiveLimitExceeded` | never produced; no receive policy is set |

### Safety

- Every export wraps its body so a panic becomes an error return. Nothing unwinds across the
  boundary.
- Every export checks for a null handle and returns the error value.
- The glue takes no locks and assumes one thread per host, as ENet does. Several hosts in one
  process work, which the tests rely on.

## Behaviour that changes

| Area | ENet-CSharp 2.4.9 | rusty_enet (dcl-one) | Effect on Pulse |
| --- | --- | --- | --- |
| Host MTU | 1280 | 1392 | Unity still gets 1280, a 1392 client gets 1392. |
| MTU agreement | grants the client's value | smaller of the two | Correct. |
| Datagram read size | host MTU (1280) | 4096 | Fixes the silent drop. |
| Timeout event | `Timeout` (4) | `Disconnect` (2), data 0 | Server: none; both run `TeardownPeerSlot`, only the debug log text differs. Test client with `--enet=rust`: a timeout prints "disconnected by server: NONE (0)" where it printed "timed out"; the reason it reports is `NONE` either way. |
| Keepalive ping | 250 ms | 500 ms | A dead peer is noticed up to 250 ms later. |
| Initial RTT | 1 ms | 500 ms | A peer's first RTT sample may read 500 ms. First retransmits on a fresh connection start slower. |
| Oversized datagram on Windows | `service` returns -1 | datagram skipped | None. |
| Dispose after successful send | no-op | double free | No caller does it. |
| Callbacks (alloc, intercept, checksum) | available | not exported | Unused by Pulse. |

`PeerTimeoutMs`, the 4095 peer limit, window sizes, throttle constants, 32 MB packet and
waiting-data limits, and the wire format (ENet-CSharp header flags, via the robtfm retarget) are
the same in both.

Where the ENet-CSharp column comes from: `Source/Native/enet.h` at SoftwareGuy/ENet-CSharp
`14c4dc5`, the build Pulse ships. `ENET_PEER_PING_INTERVAL = 250` is line 534 and
`ENET_PEER_DEFAULT_ROUND_TRIP_TIME = 1` is line 522. The comments on
`ENetTransportOptions.PeerTimeoutMs` and `ENetHostedService.SamplePeerRtt` say 500 ms for both;
they describe stock ENet and become true with this change. The MTU rows, the Windows `-1`, and a
`rusty_enet` client talking to the ENet-CSharp library on all three channels were reproduced with
a probe on 2026-11-25.

## Building and shipping the libraries

- `enet-native.yml` builds the glue for `linux-x64`, `win-x64` and `macos-arm64` with the pinned
  toolchain. It runs on a change under `native/enet/` and on manual dispatch, runs the Rust tests,
  and uploads the three libraries with their SHA-256.
- Linux builds on the oldest hosted Ubuntu runner so the library loads on the glibc of all three
  Pulse images. Windows links the C runtime statically.
- The libraries are committed to `src/DCLPulse/runtimes/…`, replacing the ENet-CSharp files, with
  `PROVENANCE.json` updated in the same commit. That is the only committed copy of the glue
  libraries; the test client takes its `enet-rust` files from there. The Dockerfiles and
  `DCLPulse.csproj` do not change.
- A check in `test.yml` recomputes the digest of `native/enet/` sources and the SHA-256 of the
  three committed libraries and compares them with `PROVENANCE.json`. Source cannot change without
  a rebuild, and a library cannot change without a record.
- `enet-pin.yml` compares the Git tree SHA of `library/vendor/rusty_enet` at the pin with the one
  on dcl-one's main. On a schedule it fails with a clear message when they differ. On pull
  requests it only warns, so a dcl-one change does not turn unrelated Pulse work red.
- Moving the pin is a documented procedure in the README: change `rev` and the tree SHA in
  `PIN.json`, run the build workflow, commit the three libraries and `PROVENANCE.json`, run the
  e2e check.

### Test client: two libraries, one chosen at start

The test client ships both libraries and loads one per process:

- `--enet=csharp` loads ENet-CSharp, the library Unity uses. It announces MTU 1280. This is the
  default, so existing commands and scripts behave as they do today.
- `--enet=rust` loads the glue library, the same ENet Bevy uses. It announces MTU 1392.

How it works:

- `DCLPulseTestClient.csproj` copies its own ENet-CSharp files to `enet-csharp/` in the output and
  the server's glue files (from `src/DCLPulse/runtimes`, by relative path) to `enet-rust/`. Neither
  set is copied to the output root any more.
- At startup, before the first ENet call, the test client registers a
  `NativeLibrary.SetDllImportResolver` for the assembly that holds `ENet.cs`. The resolver maps the
  library name `enet` to the file in the chosen folder. `ENet.cs` and `ENetTransport` do not
  change.
- The startup line that prints the ENet version also prints which library was loaded.

`DCLPulseTests` references both projects. With the test client's files in subfolders, the only
`libenet.so` or `enet.dll` in the test output root is the server's glue library, so the server
tests always run on what ships. The process can load only one library under the name `enet`, so
tests in that project never mix the two.

`scripts/e2e/livekit-takeover.sh` runs the test client from its own `bin` folder and passes no
`--enet`, so it stays on ENet-CSharp.

## Testing

Rust, in `native/enet/tests`, calling the exported functions:

- Connect, send and receive on all three channels with the flags Pulse uses; disconnect with a
  reason; `disconnect_now` produces no local event; timeout arrives as `Disconnect`.
- **The regression:** a `rusty_enet` client at MTU 1392 sends reliable messages of 1300 and 3000
  bytes and an unsequenced 1364-byte message; the glue host receives all of them. This is the case
  that fails against ENet-CSharp (reproduced on 2026-11-25).
- A client announcing MTU 1280 is granted 1280.
- `enet_peer_get_ip` returns `127.0.0.1` for a loopback IPv4 peer under both `0.0.0.0` and `::`.
- Packet ownership: send consumes, failed send does not, dispose frees.
- Null handles and a panic inside an export return errors.
- **ENet-CSharp interop:** the test loads the ENet-CSharp library from
  `src/DCLPulseTestClient/runtimes` as a client and connects it to a glue host. It is granted
  MTU 1280, exchanges packets on all three channels, sends and receives a 3000-byte reliable
  packet, and each side sees the other's disconnect reason. Runs on Linux and Windows. The e2e
  runs below need accounts and a running stack, so this is the check of the Unity pairing that
  runs on every build.

.NET:

- The existing suite, including `ENetHostedServiceShutdownTests`, passes on the glue library.
- A new `ENetNativeLibraryTests` fixture: the server grants a client MTU 1392, receives a
  3000-byte reliable packet intact through `ENet.cs`, and reports a loopback peer as `127.0.0.1`.

End to end:

- `validate-e2e` twice against the server on the glue, once with `--enet=csharp` and once with
  `--enet=rust`: handshake, movement on all three channels, graceful shutdown reason. The
  `validate-e2e` and `run-test-client` skills and the README option table gain the option.
- A .NET test for the resolver: each value of the option resolves to an existing file in its
  folder, and an unknown value is rejected with a clear message.
- A Unity client and a Bevy client against the pull-request image on a dev deployment, before
  merge. No automated Pulse run uses a real Unity client.
- The three Docker images build and start, and the server logs `ENet initialized`.

## Rollout

The ENet-CSharp libraries are replaced outright; there is no runtime switch. Rollback is the
previous image. `docker-pr.yml` publishes a pull-request image, so the change runs on a dev
deployment against Unity and Bevy clients before merge.

## Docs to update

- `README.md`: the native-dependency section gains the glue crate, the pin, and how to rebuild.
- `docs/ai-agent-context.md` if it names ENet-CSharp as the server's ENet.
- The comments on `ENetTransportOptions.PeerTimeoutMs` (ping interval) and `SamplePeerRtt`
  (initial RTT) already say 500 ms and need no change.

## Left to the builder

- The crate's internal structure and which crate provides the readable-wait and `IPV6_V6ONLY`.
- How `PROVENANCE.json` digests the sources.
- Whether the library commit is made by a person from workflow artifacts or by a pull request the
  workflow opens. Commits are GPG-signed, so the first is the default.

## Possible follow-ups, not part of this

- Log and count a negative `host.Service` result in `ENetHostedService`; today it is ignored.
- `SendToPeer` does not dispose a packet when `peer.Send` fails, which leaks it in both libraries.
- Expose dcl-one's receive limits and socket-error counters through a wider interface.
