# Rust ENet Drop-In Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Pulse's ENet-CSharp native libraries with libraries compiled from dcl-one's `rusty_enet`, as a drop-in, and let the test client run on either library.

**Architecture:** A Rust glue crate in `native/enet/` depends on dcl-one's `library/vendor/rusty_enet` at a pinned commit and exports ENet-CSharp's `enet_*` C functions. Its build output replaces the files under `src/DCLPulse/runtimes`. `ENet.cs` and the server code do not change. The test client ships both libraries in subfolders and picks one at start.

**Tech Stack:** Rust 1.99.1 (cdylib), `rusty_enet` 0.4.0 from dcl-one, .NET 10, NUnit, GitHub Actions, Python 3 for the provenance script.

**Spec:** `docs/superpowers/specs/2026-11-25-rust-enet-drop-in-design.md`

## Global Constraints

- `rusty_enet` comes only from `https://github.com/decentraland/dcl-one` at commit `43dfb8e3e14c599972d3cca0ad4c718d9052b4ad`, folder `library/vendor/rusty_enet`, Git tree SHA `bf7f72cb819bc99ab3457f75b5db07fdb6b2b376`. It is fetched into the git-ignored `native/enet/vendor/`. No copy, patch or fork of it is committed to this repository.
- The glue uses only `rusty_enet`'s public API.
- `ENet.cs`, `ENetChannel.cs`, `ENetHostedService*.cs` and `ENetTransport.cs` are not edited.
- Every exported function has ENet-CSharp's C signature, `extern "C"`, and runs its body through `guard` (Task 1). A panic or a null handle returns the error value; nothing unwinds into .NET.
- No locks in the glue. One thread drives a host.
- `enet_linked_version()` returns `0x0002_0409`.
- Library file names stay `enet.dll`, `libenet.so`, `libenet.dylib`.
- Commits are GPG-signed and are made from the main session, never with `--no-gpg-sign`. Commit messages are one short subject line.
- .NET verification commands: `dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false` and `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`.
- Rust verification commands run from `native/enet/`: `cargo test`, `cargo clippy --all-targets -- -D warnings`, `cargo fmt --check`.
- Public docs, PR text and reports name no deployment, hostname or datasource.

## Review Focus

Inputs the spec implies but does not test, most likely first. Each has a test in the task named.

1. **Bind port already in use.** `enet_host_create` returns null, so .NET throws "Host creation call failed" and does not crash. (Task 3)
2. **Send through a stale peer handle.** The server keeps `Peer` structs after a disconnect; a send on one returns a negative value and the packet handle is still disposable. (Task 4)
3. **Junk datagrams on the port.** Random bytes and a truncated ENet header are ignored and the next real connect still succeeds. (Task 3)
4. **Host name that does not resolve.** `enet_address_set_hostname` returns -1. (Task 2)
5. **Zero-length packet.** Create, send, receive and dispose a packet of length 0. (Task 4)

## File Structure

```
native/enet/
  Cargo.toml, Cargo.lock, rust-toolchain.toml, .gitignore
  PIN.json          dcl-one repository, rev, path, tree_sha
  vendor/           git-ignored, filled by scripts/enet-fetch-source.py
  PROVENANCE.json
  src/lib.rs        module list and re-exports
  src/guard.rs      panic and null guard
  src/library.rs    enet_initialize, enet_deinitialize, enet_linked_version, enet_time_get
  src/address.rs    ENetAddress, enet_address_*, address conversion and IP text
  src/socket.rs     GlueSocket: bind, rusty_enet::Socket impl, readable wait
  src/host.rs       GlueHost, PeerRecord, ENetEvent, enet_host_*
  src/peer.rs       enet_peer_*
  src/packet.rs     PacketHandle, flag mapping, enet_packet_*
  tests/common/mod.rs  loopback helpers
  tests/address.rs, tests/host.rs, tests/packets.rs, tests/mtu.rs, tests/csharp_interop.rs
scripts/enet-fetch-source.py
scripts/enet-provenance.py
.github/workflows/enet-native.yml, .github/workflows/enet-pin.yml
src/DCLPulse/runtimes/**            replaced binaries
src/DCLPulseTestClient/ENetLibrarySelector.cs
src/DCLPulseTests/ENetNativeLibraryTests.cs, src/DCLPulseTests/ENetLibrarySelectorTests.cs
```

---

### Task 1: Glue crate, source pin, library exports

**Files:**
- Create: `native/enet/PIN.json`, `scripts/enet-fetch-source.py`, `native/enet/Cargo.toml`, `native/enet/Cargo.lock`, `native/enet/rust-toolchain.toml`, `native/enet/.gitignore`, `native/enet/src/lib.rs`, `native/enet/src/guard.rs`, `native/enet/src/library.rs`
- Test: unit tests in `src/guard.rs` and `src/library.rs`; `python scripts/enet-fetch-source.py selftest`

**Interfaces:**
- Produces: `native/enet/PIN.json` = `{"repository": "https://github.com/decentraland/dcl-one", "rev": "43dfb8e3e14c599972d3cca0ad4c718d9052b4ad", "path": "library/vendor/rusty_enet", "tree_sha": "bf7f72cb819bc99ab3457f75b5db07fdb6b2b376"}`.
- Produces: `python scripts/enet-fetch-source.py fetch` (idempotent; exit 0 when `native/enet/vendor/dcl-one` holds the pinned folder and its tree SHA matches), `… verify` (no network; exit 1 naming the mismatch), `… selftest`.
- Produces: `pub(crate) fn guard<T>(on_error: T, body: impl FnOnce() -> Option<T>) -> T` — returns `on_error` when `body` returns `None` or panics. Every later export uses it.
- Produces: `enet_initialize() -> c_int` (0), `enet_deinitialize()`, `enet_linked_version() -> u32`, `enet_time_get() -> u32` (milliseconds since the first call in the process).

- [ ] **Step 1: Write the fetch script and the pin**

`scripts/enet-fetch-source.py fetch` creates `native/enet/vendor/dcl-one` as a Git repository and runs, with `core.autocrlf=false` and `core.longpaths=true` set in that repository: `git remote add origin <repository>`, `git sparse-checkout set --no-cone /<path>/`, `git fetch --depth 1 --filter=blob:none origin <rev>`, `git checkout --detach FETCH_HEAD`. It then requires `git rev-parse HEAD` to equal `rev`, `git rev-parse HEAD:<path>` to equal `tree_sha`, and `git status --porcelain` to be empty; on any mismatch it prints which and exits 1. When the directory already satisfies those three checks it does nothing. `verify` runs only the three checks. Only `<path>` may be checked out; nothing else of dcl-one lands on disk.

`selftest` builds a small local Git repository with a nested folder, pins it, and asserts: `fetch` then `verify` pass; a wrong `tree_sha` fails naming `tree_sha`; an edited fetched file fails `verify`; a second `fetch` is a no-op.

Run: `python scripts/enet-fetch-source.py selftest` → `selftest ok`. Then `python scripts/enet-fetch-source.py fetch` → exits 0 in well under five minutes; `native/enet/vendor/dcl-one/library/vendor/rusty_enet/Cargo.toml` exists and no sibling of `library` is checked out.

- [ ] **Step 2: Create the manifest**

`Cargo.toml`: package `enet`, edition 2021, `[lib] name = "enet"`, `crate-type = ["cdylib", "rlib"]` (the `rlib` lets integration tests call the exports), a `[workspace]` table with `exclude = ["vendor"]` (the vendored crate declares its own workspace), `rusty_enet = { path = "vendor/dcl-one/library/vendor/rusty_enet" }`, `[profile.release] panic = "unwind"`, `lto = true`. `rust-toolchain.toml`: `channel = "1.99.1"`, components `rustfmt`, `clippy`. `.gitignore`: `target/` and `vendor/`.

Run: `cargo tree -p rusty_enet`
Expected: `rusty_enet v0.4.0 (…/native/enet/vendor/dcl-one/library/vendor/rusty_enet)`.

- [ ] **Step 3: Write the failing tests**

```rust
#[test] fn linked_version_is_2_4_9() { assert_eq!(enet_linked_version(), (2 << 16) | (4 << 8) | 9); }
#[test] fn initialize_succeeds() { assert_eq!(enet_initialize(), 0); enet_deinitialize(); }
#[test] fn time_does_not_go_backwards() { let a = enet_time_get(); let b = enet_time_get(); assert!(b >= a); }
#[test] fn guard_returns_error_value_on_none() { assert_eq!(guard(-1, || None::<i32>), -1); }
#[test] fn guard_returns_error_value_on_panic() { assert_eq!(guard(-1, || -> Option<i32> { panic!("boom") }), -1); }
#[test] fn guard_returns_body_value() { assert_eq!(guard(-1, || Some(7)), 7); }
```

- [ ] **Step 4: Run them to see them fail**

Run: `cargo test`
Expected: compile errors for the missing functions.

- [ ] **Step 5: Implement `guard` and the four library exports**

`guard` uses `std::panic::catch_unwind` with `AssertUnwindSafe`.

- [ ] **Step 6: Run tests, clippy and fmt**

Run: `cargo test && cargo clippy --all-targets -- -D warnings && cargo fmt --check`
Expected: 6 passed, no warnings.

- [ ] **Step 7: Commit**

`git add native/enet scripts/enet-fetch-source.py && git commit -m "feat: add ENet glue crate pinned to dcl-one"`

---

### Task 2: Addresses and the socket

**Files:**
- Create: `native/enet/src/address.rs`, `native/enet/src/socket.rs`, `native/enet/tests/address.rs`
- Modify: `native/enet/src/lib.rs`, `native/enet/Cargo.toml` (socket dependencies)

**Interfaces:**
- Produces: `#[repr(C)] pub struct ENetAddress { pub ipv6: [u8; 16], pub port: u16 }` (18 bytes; IPv4 is stored as `::ffff:a.b.c.d`).
- Produces: `enet_address_set_ip(address: *mut ENetAddress, ip: *const c_char) -> c_int`, `enet_address_set_hostname(address: *mut ENetAddress, name: *const c_char) -> c_int`, `enet_address_get_ip(address: *const ENetAddress, ip: *mut c_char, ip_length: usize) -> c_int`. 0 on success, -1 on failure.
- Produces: `pub(crate) fn to_socket_addr(a: &ENetAddress) -> SocketAddr` (mapped IPv4 becomes `SocketAddr::V4`), `pub(crate) fn from_socket_addr(s: SocketAddr) -> ENetAddress`, `pub(crate) fn ip_text(s: SocketAddr) -> String` (dotted form for IPv4 and mapped IPv4, IPv6 text otherwise).
- Produces: `pub(crate) struct GlueSocket` with `fn bind(address: Option<SocketAddr>) -> io::Result<GlueSocket>`, `fn wait_readable(&self, timeout: Duration) -> bool`, `fn local_addr(&self) -> io::Result<SocketAddr>`, and `impl rusty_enet::Socket for GlueSocket` with `Address = SocketAddr`, `Error = io::Error`.

Socket rules from the spec:
- `bind(None)` binds `0.0.0.0:0`. An IPv4 address binds an IPv4 socket. An IPv6 address binds an IPv6 socket with `IPV6_V6ONLY` off.
- `init` sets non-blocking, broadcast, and the buffer sizes from `SocketOptions`.
- `send`: `WouldBlock` and every other error return `Ok(0)`.
- `receive`: `WouldBlock` returns `Ok(None)`. `ConnectionReset`, `ConnectionRefused`, `HostUnreachable`, `NetworkUnreachable`, and on Windows `WSAEMSGSIZE` (10040) and `WSAENETRESET` (10052), skip that datagram and read again, at most 16 times per call. Any other error is returned.
- A peer address seen on a dual-stack socket as `::ffff:a.b.c.d` is reported to `rusty_enet` as `SocketAddr::V4`, and converted back on send, so one host always has one form per peer.

- [ ] **Step 1: Write the failing tests** (`tests/address.rs`)

```rust
#[test] fn set_ip_v4_is_stored_mapped()       // "127.0.0.1" -> ipv6 == [0;10] ++ [0xFF,0xFF,127,0,0,1], returns 0
#[test] fn set_ip_v6_is_stored_as_is()        // "::1" -> ipv6[15] == 1, others 0
#[test] fn set_ip_rejects_garbage()           // "not-an-ip" -> -1
#[test] fn get_ip_prints_mapped_v4_dotted()   // set "10.1.2.3", get -> "10.1.2.3"
#[test] fn get_ip_prints_v6()                 // set "::1", get -> "::1"
#[test] fn get_ip_fails_when_buffer_too_small() // ip_length 4 for "10.1.2.3" -> -1
#[test] fn set_hostname_resolves_localhost()  // "localhost" -> 0, text is "127.0.0.1" or "::1"
#[test] fn set_hostname_fails_for_unresolvable() // "no-such-host.invalid" -> -1
#[test] fn set_hostname_accepts_ip_literal()  // "192.0.2.7" -> 0, text "192.0.2.7"
#[test] fn null_pointers_return_error()       // each of the three with null -> -1
#[test] fn socket_binds_v4_and_reports_port() // bind 127.0.0.1:0 -> local_addr port != 0
#[test] fn dual_stack_socket_receives_v4()    // bind [::]:0; std UdpSocket sends from 127.0.0.1; receive yields SocketAddr::V4(127.0.0.1)
#[test] fn wait_readable_times_out()          // no traffic, 20 ms -> false, elapsed >= 15 ms
#[test] fn wait_readable_wakes_on_datagram()  // datagram queued -> true
```

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --test address`
Expected: compile errors.

- [ ] **Step 3: Implement `address.rs`**

`enet_address_set_hostname` resolves with `ToSocketAddrs` on `(name, 0)` and takes the first result; an IP literal resolves to itself.

- [ ] **Step 4: Implement `socket.rs`**

Pick the crates for `IPV6_V6ONLY` and the readable wait (`socket2` and a poll call are enough); keep them out of the public interface.

- [ ] **Step 5: Run tests, clippy, fmt**

Run: `cargo test && cargo clippy --all-targets -- -D warnings && cargo fmt --check`
Expected: all pass.

- [ ] **Step 6: Commit**

`git commit -m "feat: add ENet glue addresses and socket"`

---

### Task 3: Hosts, events and peer queries

**Files:**
- Create: `native/enet/src/host.rs`, `native/enet/src/peer.rs`, `native/enet/tests/common/mod.rs`, `native/enet/tests/host.rs`
- Modify: `native/enet/src/lib.rs`

**Interfaces:**
- Consumes: `GlueSocket`, `ENetAddress`, `to_socket_addr`, `ip_text`, `guard`.
- Produces: `pub struct GlueHost` (owns `rusty_enet::Host<GlueSocket>` and a `Box<[PeerRecord]>` of `peer_limit` entries) and `#[repr(C)] pub struct PeerRecord { host: *mut GlueHost, index: u32 }`. A `PeerRecord` address is stable until `enet_host_destroy`.
- Produces: `#[repr(C)] pub struct ENetEvent { pub kind: c_int, pub peer: *mut PeerRecord, pub channel_id: u8, pub data: u32, pub packet: *mut PacketHandle }` with kinds 0 none, 1 connect, 2 disconnect, 3 receive. `PacketHandle` is declared here as an opaque type and defined in Task 4; until then a `Receive` event is dropped.
- Produces: `enet_host_create(address: *const ENetAddress, peer_count: usize, channel_limit: usize, incoming_bandwidth: u32, outgoing_bandwidth: u32, buffer_size: c_int) -> *mut GlueHost` (null on failure; `channel_limit` 0 means 255; bandwidth 0 means unlimited; `buffer_size` is ignored), `enet_host_destroy(host: *mut GlueHost)`, `enet_host_service(host: *mut GlueHost, event: *mut ENetEvent, timeout: u32) -> c_int`, `enet_host_check_events(host: *mut GlueHost, event: *mut ENetEvent) -> c_int`, `enet_host_flush(host: *mut GlueHost)`, `enet_host_connect(host: *mut GlueHost, address: *const ENetAddress, channel_count: usize, data: u32) -> *mut PeerRecord`.
- Produces: `enet_peer_get_id(peer: *const PeerRecord) -> u32`, `enet_peer_get_ip(peer: *const PeerRecord, ip: *mut c_char, ip_length: usize) -> c_int`, `enet_peer_get_port(peer: *const PeerRecord) -> u16`, `enet_peer_get_mtu(peer: *const PeerRecord) -> u32`, `enet_peer_get_state(peer: *const PeerRecord) -> c_int` (0 disconnected … 9 zombie, in `rusty_enet::PeerState` order), `enet_peer_get_rtt(peer: *const PeerRecord) -> u32` (milliseconds), `enet_peer_timeout(peer: *mut PeerRecord, limit: u32, minimum: u32, maximum: u32)`, `enet_peer_ping_interval(peer: *mut PeerRecord, interval: u32)`.
- Produces (test helper): `tests/common/mod.rs` with `fn server(bind: &str) -> (*mut GlueHost, u16)`, `fn client() -> *mut GlueHost`, `fn connect(client, port) -> *mut PeerRecord`, `fn pump_until(hosts: &[*mut GlueHost], deadline_ms: u64, pred: impl FnMut(*mut GlueHost, &ENetEvent) -> bool) -> bool`.

Service rules: return 1 with one event, 0 on timeout, -1 on a socket error or null handle. Call `Host::service`; when it yields no event and time remains, `wait_readable` for the remainder and call it again. `timeout` 0 never waits. `ReceiveLimitExceeded` is ignored.

- [ ] **Step 1: Write the failing tests** (`tests/host.rs`)

```rust
#[test] fn client_and_server_see_connect()            // both get kind 1; server event.peer id == 0
#[test] fn connect_data_reaches_server()              // enet_host_connect(.., data 77) -> server connect event.data == 77
#[test] fn peer_ip_is_dotted_v4_under_v4_bind()       // server bound "0.0.0.0": enet_peer_get_ip == "127.0.0.1"
#[test] fn peer_ip_is_dotted_v4_under_v6_bind()       // server bound "::": enet_peer_get_ip == "127.0.0.1"
#[test] fn peer_port_matches_client_socket()
#[test] fn glue_client_is_granted_mtu_1392()          // server-side enet_peer_get_mtu == 1392
#[test] fn peer_state_is_connected_after_connect()    // == 5
#[test] fn rtt_is_reported_in_milliseconds()          // after connect and 200 ms of servicing, rtt <= 500
#[test] fn service_times_out_with_zero()              // idle host, timeout 30 -> returns 0 after >= 20 ms
#[test] fn service_with_zero_timeout_does_not_wait()  // returns 0 in < 10 ms
#[test] fn create_fails_when_port_is_taken()          // second host on the same port -> null
#[test] fn create_fails_above_peer_limit()            // peer_count 4096 -> null
#[test] fn junk_datagrams_are_ignored()               // send [0xFF; 7] and a 2-byte datagram to the server port; service returns 0; a real connect then succeeds
#[test] fn peer_record_survives_until_destroy()       // keep the connect event's peer pointer, let the client vanish and time out, enet_peer_get_id still returns the slot
#[test] fn two_hosts_in_one_process_are_independent()
#[test] fn null_handles_return_errors()               // service -> -1, check_events -> -1, connect -> null, get_id -> 0, get_ip -> -1
```

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --test host`
Expected: compile errors.

- [ ] **Step 3: Implement `host.rs` and `peer.rs`**

- [ ] **Step 4: Run tests, clippy, fmt**

Run: `cargo test && cargo clippy --all-targets -- -D warnings && cargo fmt --check`
Expected: all pass.

- [ ] **Step 5: Commit**

`git commit -m "feat: add ENet glue hosts and peers"`

---

### Task 4: Packets, sends and disconnects

**Files:**
- Create: `native/enet/src/packet.rs`, `native/enet/tests/packets.rs`, `native/enet/tests/mtu.rs`
- Modify: `native/enet/src/host.rs` (deliver `Receive`), `native/enet/src/peer.rs`, `native/enet/src/lib.rs`

**Interfaces:**
- Consumes: `GlueHost`, `PeerRecord`, `ENetEvent`, `guard`, the test helpers.
- Produces: `pub struct PacketHandle(rusty_enet::Packet)`, handed out as `Box::into_raw`.
- Produces: `enet_packet_create(data: *const u8, data_length: usize, flags: u32) -> *mut PacketHandle`, `enet_packet_create_offset(data: *const u8, data_length: usize, data_offset: usize, flags: u32) -> *mut PacketHandle` (copies `data_length - data_offset` bytes from `data + data_offset`, as ENet-CSharp does; null when the offset exceeds the length), `enet_packet_get_data(packet: *const PacketHandle) -> *const u8`, `enet_packet_get_length(packet: *const PacketHandle) -> c_int`, `enet_packet_dispose(packet: *mut PacketHandle)`.
- Produces: `enet_peer_send(peer: *mut PeerRecord, channel_id: u8, packet: *mut PacketHandle) -> c_int` (0 consumes the handle; -1 leaves it with the caller), `enet_peer_disconnect(peer: *mut PeerRecord, data: u32)`, `enet_peer_disconnect_now(peer: *mut PeerRecord, data: u32)`, `enet_peer_disconnect_later(peer: *mut PeerRecord, data: u32)`, `enet_peer_reset(peer: *mut PeerRecord)`.

Flag bits: `RELIABLE 1`, `UNSEQUENCED 2`, `NO_ALLOCATE 4`, `UNRELIABLE_FRAGMENTED 8`, `INSTANT 16`, `UNTHROTTLED 32`. Mapping is the table in the spec's "Flags" section. `INSTANT` calls `Host::flush` after a successful send. A null `data` with length 0 creates an empty packet.

- [ ] **Step 1: Write the failing tests**

`tests/packets.rs`:

```rust
#[test] fn reliable_packet_arrives_on_channel_0()        // flags 1, 100 bytes, first byte 0xA1 -> receive event channel 0, length 100, byte 0xA1
#[test] fn unthrottled_sequenced_arrives_on_channel_1()  // flags 32
#[test] fn unsequenced_arrives_on_channel_2()            // flags 2
#[test] fn unthrottled_burst_arrives_in_order()          // 8 flag-32 packets on channel 1 in one burst, first bytes 0..7 -> all 8 arrive in that order
#[test] fn zero_length_packet_round_trips()              // create(null, 0, 1) -> receive length 0; dispose
#[test] fn create_offset_copies_the_tail()               // data [1,2,3,4,5], length 5, offset 2 -> packet [3,4,5]
#[test] fn create_offset_rejects_offset_past_length()    // offset 6 -> null
#[test] fn send_consumes_the_handle()                    // send returns 0; the test does not dispose; run under the leak/ASAN-free default and assert the packet arrives
#[test] fn failed_send_leaves_the_handle()               // channel 9 on a 3-channel peer -> -1; enet_packet_get_length still valid; dispose
#[test] fn send_on_stale_peer_handle_fails()             // peer disconnected and slot idle -> -1, dispose ok
#[test] fn disconnect_reason_reaches_the_other_side()    // server enet_peer_disconnect(peer, 16) -> client kind 2, data 16; server later gets kind 2 for that peer
#[test] fn disconnect_now_sends_reason_and_no_local_event() // server disconnect_now(peer, 1) -> client kind 2 data 1; server check_events returns 0
#[test] fn disconnect_later_flushes_queued_packets_first() // send reliable then disconnect_later(3) -> client gets the packet, then kind 2 data 3
#[test] fn reset_drops_the_peer_silently()               // no event on either side at once; state == 0
#[test] fn timeout_arrives_as_disconnect()               // enet_peer_timeout(peer, 0, 500, 500); drop the client host without disconnecting; server gets kind 2, data 0 within 3 s
#[test] fn null_handles_return_errors()                  // send -> -1, get_length -> 0, get_data -> null, dispose(null) is a no-op
```

`tests/mtu.rs` — the regression. The client is a raw `rusty_enet::Host<std::net::UdpSocket>` with `HostSettings { peer_limit: 1, channel_limit: 3, ..Default::default() }`:

```rust
#[test] fn client_at_1392_delivers_1300_byte_reliable()
#[test] fn client_at_1392_delivers_3000_byte_reliable()   // fragments are 1392-byte datagrams
#[test] fn client_at_1392_delivers_1364_byte_unsequenced() // one 1374-byte datagram
#[test] fn client_at_1280_is_granted_1280()                // client.set_mtu(1280) before connect -> server enet_peer_get_mtu == 1280 and client peer.mtu() == 1280
#[test] fn server_sends_3000_byte_reliable_to_1280_client() // arrives intact
```

Each delivery test asserts the received length and that every byte equals `(index % 251) as u8`.

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --test packets --test mtu`
Expected: compile errors.

- [ ] **Step 3: Implement `packet.rs`, the `Receive` event, and the peer send and disconnect exports**

- [ ] **Step 4: Run tests, clippy, fmt**

Run: `cargo test && cargo clippy --all-targets -- -D warnings && cargo fmt --check`
Expected: all pass.

- [ ] **Step 5: Check the export list**

Run (Linux): `cargo build --release && nm -D --defined-only target/release/libenet.so | grep ' T enet_' | wc -l`
Expected: `31` — the functions in the spec's "Exported functions" table and nothing else named `enet_*`. On Windows use `dumpbin /exports target\release\enet.dll` and count the `enet_` lines.

- [ ] **Step 6: Commit**

`git commit -m "feat: add ENet glue packets, sends and disconnects"`

---

### Task 5: ENet-CSharp interop test

**Files:**
- Create: `native/enet/tests/csharp_interop.rs`
- Modify: `native/enet/Cargo.toml` (`libloading` as a dev-dependency)

**Interfaces:**
- Consumes: the glue exports and test helpers; the ENet-CSharp libraries at `../../src/DCLPulseTestClient/runtimes/{win-x64/native/enet.dll,linux-x64/native/libenet.so}` relative to the crate.

The test loads the ENet-CSharp library with `libloading` and calls it as a client through its own `enet_*` symbols (`enet_initialize`, `enet_host_create` with a null address, `enet_address_set_ip`, `enet_host_connect`, `enet_host_service`, `enet_packet_create`, `enet_peer_send`, `enet_packet_get_data`, `enet_packet_get_length`, `enet_packet_dispose`, `enet_peer_get_mtu`, `enet_peer_disconnect`, `enet_host_destroy`). Its `ENetAddress` is passed as a 20-byte buffer with the port at offset 16. The file is `#![cfg(any(target_os = "linux", target_os = "windows"))]`.

- [ ] **Step 1: Write the tests**

```rust
#[test] fn fixture_hashes_match()   // sha256(libenet.so) == 8269fcb7ec49d2da4d31fa93baa6833a5ac16de604229a2197a80bfb49f6c537, sha256(enet.dll) == 0a03cd54b35c77b679ce81f9c5d2e903486a4cd1500b3b4b21edfe4b0260dba3
#[test] fn csharp_client_connects_and_is_granted_1280()   // both sides report MTU 1280
#[test] fn csharp_client_packets_arrive_on_all_three_channels() // flags 1 on ch 0, 32 on ch 1, 2 on ch 2, 32 bytes each
#[test] fn csharp_client_3000_byte_reliable_arrives()
#[test] fn glue_host_3000_byte_reliable_reaches_csharp_client()
#[test] fn glue_host_packets_reach_csharp_client_on_all_three_channels()
#[test] fn disconnect_reasons_cross_both_ways()   // glue disconnect(16) -> client event data 16; in a second connection client disconnect(5) -> glue event kind 2 data 5
```

- [ ] **Step 2: Run them**

Run: `cargo test --test csharp_interop`
Expected: all pass. A failure here is an interop bug in the glue; fix the glue, not the test.

- [ ] **Step 3: Commit**

`git commit -m "test: check ENet glue against ENet-CSharp client"`

---

### Task 6: Provenance script and CI

**Files:**
- Create: `scripts/enet-provenance.py`, `.github/workflows/enet-native.yml`, `.github/workflows/enet-pin.yml`

`PROVENANCE.json` and the `test.yml` check land in Task 7 with the libraries, so `main` never carries a failing check.

**Interfaces:**
- Produces: `python scripts/enet-provenance.py write` and `python scripts/enet-provenance.py check` (exit 0 or 1 with a message naming what differs).
- Produces: `PROVENANCE.json` with keys `dcl_one_rev`, `rusty_enet_tree_sha`, `toolchain`, `source_digest`, `libraries` (`{"src/DCLPulse/runtimes/linux-x64/native/libenet.so": "<sha256>", …}` for the three files).

Definitions:
- `source_digest` is the SHA-256 of the output of `git ls-files -s -- native/enet ":!native/enet/PROVENANCE.json"`. It uses index blob hashes, so line endings do not affect it.
- `dcl_one_rev` and `rusty_enet_tree_sha` are copied from `native/enet/PIN.json` (`rev`, `tree_sha`); `toolchain` from `rust-toolchain.toml`.
- `check` recomputes `source_digest`, the three library hashes, `dcl_one_rev` and `toolchain`, and compares.

- [ ] **Step 1: Write the script and its self-test**

`python scripts/enet-provenance.py selftest` builds a temporary git repository with two files, writes provenance, asserts `check` passes, changes one byte of a library, and asserts `check` fails naming that library; then changes a source file and asserts `check` fails naming `source_digest`.

- [ ] **Step 2: Run the self-test**

Run: `python scripts/enet-provenance.py selftest`
Expected: `selftest ok`.

- [ ] **Step 3: Write `enet-native.yml`**

Triggers: `workflow_dispatch`, and `pull_request` on paths `native/enet/**`. Matrix: `ubuntu-22.04` / `x86_64-unknown-linux-gnu` / `libenet.so`; `windows-latest` / `x86_64-pc-windows-msvc` / `enet.dll` with `RUSTFLAGS=-C target-feature=+crt-static`; `macos-latest` / `aarch64-apple-darwin` / `libenet.dylib`. Each job: checkout, `python scripts/enet-fetch-source.py fetch`, `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, `cargo test`, `cargo build --release`, print the SHA-256, upload the library as artifact `enet-<rid>`. `permissions: contents: read`.

- [ ] **Step 4: Write `enet-pin.yml`**

Triggers: `schedule` (daily), `workflow_dispatch`, `pull_request`. It reads `tree_sha` from `native/enet/PIN.json`, fetches the tree SHA of `library/vendor/rusty_enet` on dcl-one's `main` (`GET /repos/decentraland/dcl-one/contents/library/vendor?ref=main`), and compares the two. On a difference: `::error::dcl-one's rusty_enet tree moved; follow "Moving the ENet pin" in the README` and exit 1 for `schedule` and `workflow_dispatch`; `::warning::` with the same text and exit 0 for `pull_request`.

- [ ] **Step 5: Commit**

`git commit -m "ci: build ENet glue libraries and watch the dcl-one pin"`

---

### Task 7: Swap the server libraries

**Files:**
- Modify: `src/DCLPulse/runtimes/win-x64/native/enet.dll`, `src/DCLPulse/runtimes/linux-x64/native/libenet.so`, `src/DCLPulse/runtimes/macos-arm64/native/libenet.dylib`, `native/enet/PROVENANCE.json`, `.github/workflows/test.yml`
- Create: `src/DCLPulseTests/ENetNativeLibraryTests.cs`

**Interfaces:**
- Consumes: the three artifacts of `enet-native.yml`.

This task needs the branch pushed so the workflow can run. Pushing and the binary commit are the user's to approve.

- [ ] **Step 1: Write the failing .NET test**

`ENetNativeLibraryTests` (`[TestFixture]`, `[NonParallelizable]`, same `OneTimeSetUp`/`TearDown` and port helper pattern as `ENetHostedServiceShutdownTests`):

```csharp
[Test] public void ClientAnnouncesMtu1392_AndServerGrantsIt()
// server.Create(addr "0.0.0.0", peerLimit 4, channelLimit 3); client.Create(null, 1, 3); connect; drive both.
// Assert.That(serverSidePeer.MTU, Is.EqualTo(1392u));

[Test] public void Reliable3000BytePacket_ArrivesIntact()
// client sends 3000 bytes (data[i] = (byte)(i % 251)) with PacketFlags.Reliable on channel 0.
// Server Receive event: Packet.Length == 3000 and CopyTo matches byte for byte.

[Test] public void PeerIp_IsDottedIPv4()
// Assert.That(serverSidePeer.IP, Is.EqualTo("127.0.0.1"));
```

- [ ] **Step 2: Run it on the old libraries**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter ENetNativeLibraryTests`
Expected: `ClientAnnouncesMtu1392_AndServerGrantsIt` FAILS with 1280; the other two pass.

- [ ] **Step 3: Get the libraries**

Run `enet-native.yml` on the branch, download the three artifacts, and copy them over the three files. Check each SHA-256 against the workflow log.

- [ ] **Step 4: Write provenance and enable the check**

Run: `git add src/DCLPulse/runtimes native/enet && python scripts/enet-provenance.py write && python scripts/enet-provenance.py check`
Expected: `provenance ok`. Add a step to `.github/workflows/test.yml` before `Restore`: `python scripts/enet-provenance.py check`.

- [ ] **Step 5: Run the whole .NET suite**

Run: `dotnet build src/DCLPulse/DCLPulse.sln -p:GenerateProto=false` then `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
Expected: all pass, including `ENetHostedServiceShutdownTests` and the three new tests.

- [ ] **Step 6: Build and start the three images**

```sh
docker build -f src/DCLPulse/Dockerfile -t pulse-prod-test .
docker build -f src/DCLPulse/Dockerfile.dev-debug -t pulse-dev-debug-test .
docker build -f Dockerfile.debug -t pulse-debug-test .
```

Start each and expect the log line `ENet initialized (version 132105).` and `ENet host listening on`. A load failure here means the Linux library needs an older glibc target; fix the workflow runner or target, rebuild, repeat from Step 3.

- [ ] **Step 7: Commit**

`git commit -m "feat: run the ENet server on dcl-one's rusty_enet"`

---

### Task 8: Test client on either library

**Files:**
- Create: `src/DCLPulseTestClient/ENetLibrarySelector.cs`, `src/DCLPulseTests/ENetLibrarySelectorTests.cs`
- Modify: `src/DCLPulseTestClient/DCLPulseTestClient.csproj:38-49`, `src/DCLPulseTestClient/ClientOptions.cs`, `src/DCLPulseTestClient/Program.cs`

**Interfaces:**
- Produces: `ClientOptions.ENetLibrary` (`string`, default `"csharp"`, from `--enet=<csharp|rust>`).
- Produces:

```csharp
namespace PulseTestClient;

public static class ENetLibrarySelector
{
    public const string CSHARP = "csharp";
    public const string RUST = "rust";

    /// Returns the full path of the native library for <paramref name="choice" /> under <paramref name="baseDirectory" />.
    /// Throws ArgumentException for an unknown choice and FileNotFoundException when the file is absent.
    public static string ResolvePath(string choice, string baseDirectory);

    /// Registers the DllImport resolver for the assembly that holds ENet.cs. Call once, before any ENet call.
    public static void Use(string choice);
}
```

Layout in the test client's output: `enet-csharp/{enet.dll,libenet.so,libenet.dylib}` from its own `runtimes`, and `enet-rust/{…}` from `..\DCLPulse\runtimes`. No ENet library in the output root. `ResolvePath` picks the file name by `OperatingSystem.IsWindows()` / `IsMacOS()` / otherwise Linux. `Use` calls `NativeLibrary.SetDllImportResolver(typeof(ENet.Library).Assembly, …)` and answers only the name `enet`.

- [ ] **Step 1: Write the failing tests** (`ENetLibrarySelectorTests`)

```csharp
[TestCase("csharp", "enet-csharp")]
[TestCase("rust", "enet-rust")]
public void ResolvePath_PointsIntoTheChosenFolder(string choice, string folder)
// baseDirectory = AppContext.BaseDirectory; result exists and its directory name == folder.

[Test] public void ResolvePath_IsCaseInsensitive()        // "RUST" resolves like "rust"
[Test] public void ResolvePath_RejectsUnknownChoice()     // "unity" -> ArgumentException whose message lists "csharp" and "rust"
[Test] public void ResolvePath_ReportsMissingFile()       // empty temp dir -> FileNotFoundException naming the expected path
[Test] public void TheTwoLibrariesDiffer()                // file bytes of the csharp and rust paths are not equal
[Test] public void OutputRootHasOnlyTheServerLibrary()    // the root enet library's bytes equal the enet-rust one
[Test] public void FromArgs_DefaultsToCsharp()            // ClientOptions.FromArgs([]).ENetLibrary == "csharp"
[Test] public void FromArgs_ReadsEnetOption()             // ["--enet=rust"] -> "rust"
```

The tests never call `Use`: a resolver can be set once per assembly, and the test process must keep the server's library.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false --filter ENetLibrarySelectorTests`
Expected: compile errors.

- [ ] **Step 3: Change the csproj item group, add the option and the selector**

- [ ] **Step 4: Call it from `Program.cs`**

In the `if (!useWebTransport)` block, before `new ENetTransport(...)`: `ENetLibrarySelector.Use(options.ENetLibrary);`. After the existing `Transport:` line print `ENet library: {options.ENetLibrary}` when ENet is in use. An invalid value ends the process with the exception's message and exit code 2. Pass `--enet` through to child workers where `Program.cs` re-launches itself for large bot counts.

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/DCLPulse/DCLPulse.sln -p:GenerateProto=false`
Expected: all pass.

- [ ] **Step 6: Run the client once per mode against a local server**

Start the server, then `dotnet run --project src/DCLPulseTestClient -p:GenerateProto=false -- --enet=csharp` and again with `--enet=rust`.
Expected: each prints its `ENet library:` line and connects; the server's debug log shows the peer.

- [ ] **Step 7: Commit**

`git commit -m "feat: let the test client pick its ENet library"`

---

### Task 9: Docs, skills and end-to-end checks

**Files:**
- Modify: `README.md` (native dependencies, CLI arguments table), `docs/ai-agent-context.md` (only if it names ENet-CSharp as the server's ENet), `.claude/skills/validate-e2e/SKILL.md`, `.claude/skills/run-test-client/SKILL.md`

- [ ] **Step 1: README**

Add a section "ENet native library" after the RustEthereum one: what `native/enet` is, that the source is dcl-one at the pinned commit, that `ENet.cs` is unchanged, how to build it locally (`python scripts/enet-fetch-source.py fetch`, then `cargo build --release` in `native/enet`), and "Moving the ENet pin": change `rev` and `tree_sha` in `native/enet/PIN.json`, run `enet-native.yml`, copy the three artifacts, run `python scripts/enet-provenance.py write`, run the tests and the e2e check in both modes, commit. Add the table row: `--enet=<csharp|rust>` | `csharp` | `ENet library the client loads: ENet-CSharp (as Unity) or dcl-one's rusty_enet (as Bevy)`.

- [ ] **Step 2: Skills**

`validate-e2e` runs its flow twice, once with `--enet=csharp` and once with `--enet=rust`. `run-test-client` documents the option. Use the `writing-for-agents` skill for these edits.

- [ ] **Step 3: Run `validate-e2e` in both modes**

Expected: handshake, movement on all three channels and the graceful shutdown reason pass in each mode.

- [ ] **Step 4: Re-run the earlier probe against the new library**

Run the MTU probe from the 2026-11-25 investigation (`server.py` on the new `enet.dll`, with its Rust client). It is kept outside the repository; ask the user for its folder.
Expected: the server grants 1392 and receives the 1281, 1300 and 1374-byte datagrams it dropped before. Record the output in the PR description, not in the repo.

- [ ] **Step 5: Commit**

`git commit -m "docs: describe the ENet glue library and test client modes"`

- [ ] **Step 6: Before merge, on the pull-request image**

A Unity client and a Bevy client connect to a dev deployment running the PR image and exchange movement. This is manual and needs the user; record the result in the PR.
