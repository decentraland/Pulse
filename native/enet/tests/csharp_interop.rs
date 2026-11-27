//! Interop: the glue as a server against the real ENet-CSharp native library as a client.
//!
//! Both libraries are named `enet.dll` on Windows, so this file is its own test process and the
//! ENet-CSharp library is loaded by path. A handle never crosses from one library to the other.
#![cfg(any(target_os = "linux", target_os = "windows"))]

mod common;

use std::ffi::{c_char, c_int, c_void, CString};
use std::path::PathBuf;
use std::ptr;
use std::slice;
use std::sync::{Mutex, MutexGuard, OnceLock};
use std::time::{Duration, Instant};

use common::{empty_event, server};
use enet::{
    enet_host_destroy, enet_host_service, enet_packet_create, enet_packet_dispose,
    enet_packet_get_data, enet_packet_get_length, enet_peer_disconnect, enet_peer_get_mtu,
    enet_peer_send, GlueHost, PeerRecord,
};
use sha2::{Digest, Sha256};

const CONNECT: i32 = 1;
const DISCONNECT: i32 = 2;
const RECEIVE: i32 = 3;
const RELIABLE: u32 = 1;
const UNSEQUENCED: u32 = 2;
const UNTHROTTLED: u32 = 32;
const CHANNELS: usize = 3;
const DEADLINE_MS: u64 = 3000;
const ADDRESS_BYTES: usize = 20;
const ADDRESS_PORT_OFFSET: usize = 16;

#[cfg(target_os = "windows")]
const CSHARP_LIBRARY: &str = "win-x64/native/enet.dll";
#[cfg(target_os = "linux")]
const CSHARP_LIBRARY: &str = "linux-x64/native/libenet.so";

const LINUX_LIBRARY_SHA256: &str =
    "8269fcb7ec49d2da4d31fa93baa6833a5ac16de604229a2197a80bfb49f6c537";
const WINDOWS_LIBRARY_SHA256: &str =
    "0a03cd54b35c77b679ce81f9c5d2e903486a4cd1500b3b4b21edfe4b0260dba3";

/// ENet-CSharp's `ENetEvent`: an enum-sized type, a peer, a channel, data and a packet.
#[repr(C)]
struct CEvent {
    kind: i32,
    peer: *mut c_void,
    channel: u8,
    data: u32,
    packet: *mut c_void,
}

/// Held by every test that calls into ENet-CSharp, so only one thread uses that library at a time.
///
/// ENet-CSharp's clock is not safe to start from several threads at once. On Windows its
/// `clock_gettime` stand-in initialises plain statics lazily and sets `initialized` before the
/// counter offset and frequency, and on every platform `enet_time_get` picks its start time with
/// a compare-and-swap against a clock value read before it. When the first calls race, a thread
/// can get a time that is far off. `enet_host_service` then computes its wait as
/// `ENET_TIME_DIFFERENCE(timeout, serviceTime)`, which can reach a day, and blocks in the socket
/// wait instead of returning after 1 ms. Under parallel test threads that hung about one run in
/// ten; reading the clock once before any test ran removed the hang.
static CSHARP_LOCK: Mutex<()> = Mutex::new(());

/// Takes [`CSHARP_LOCK`]; a test that panicked while holding it does not fail the others.
fn csharp_lock() -> MutexGuard<'static, ()> {
    CSHARP_LOCK
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// The ENet-CSharp entry points the tests call, resolved once and valid for the whole process.
struct Csharp {
    address_set_ip: unsafe extern "C" fn(*mut u8, *const c_char) -> c_int,
    host_create: unsafe extern "C" fn(*const u8, usize, usize, u32, u32, c_int) -> *mut c_void,
    host_connect: unsafe extern "C" fn(*mut c_void, *const u8, usize, u32) -> *mut c_void,
    host_service: unsafe extern "C" fn(*mut c_void, *mut CEvent, u32) -> c_int,
    host_destroy: unsafe extern "C" fn(*mut c_void),
    packet_create: unsafe extern "C" fn(*const u8, usize, u32) -> *mut c_void,
    packet_get_data: unsafe extern "C" fn(*mut c_void) -> *const u8,
    packet_get_length: unsafe extern "C" fn(*mut c_void) -> c_int,
    packet_dispose: unsafe extern "C" fn(*mut c_void),
    peer_send: unsafe extern "C" fn(*mut c_void, u8, *mut c_void) -> c_int,
    peer_get_mtu: unsafe extern "C" fn(*mut c_void) -> u32,
    peer_disconnect: unsafe extern "C" fn(*mut c_void, u32),
}

fn fixture_path(relative: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../src/DCLPulseTestClient/runtimes")
        .join(relative)
}

fn sha256_hex(path: &PathBuf) -> String {
    let bytes = std::fs::read(path).unwrap_or_else(|error| panic!("{}: {error}", path.display()));
    format!("{:x}", Sha256::digest(bytes))
}

/// Loads ENet-CSharp once and initializes it; it is never deinitialized because tests run in
/// parallel.
fn csharp() -> &'static Csharp {
    static LIBRARY: OnceLock<Csharp> = OnceLock::new();
    LIBRARY.get_or_init(|| {
        let path = fixture_path(CSHARP_LIBRARY);
        // SAFETY: loading the ENet-CSharp library runs no unsound initializer.
        let library = Box::leak(Box::new(
            unsafe { libloading::Library::new(&path) }
                .unwrap_or_else(|error| panic!("{} did not load: {error}", path.display())),
        ));
        macro_rules! symbol {
            ($name:literal) => {
                // SAFETY: the symbol has the C signature declared by the field it fills, and
                // the leaked library keeps it loaded for the rest of the process.
                *unsafe { library.get($name) }
                    .unwrap_or_else(|error| panic!("{} missing: {error}", stringify!($name)))
            };
        }
        let csharp = Csharp {
            address_set_ip: symbol!(b"enet_address_set_ip"),
            host_create: symbol!(b"enet_host_create"),
            host_connect: symbol!(b"enet_host_connect"),
            host_service: symbol!(b"enet_host_service"),
            host_destroy: symbol!(b"enet_host_destroy"),
            packet_create: symbol!(b"enet_packet_create"),
            packet_get_data: symbol!(b"enet_packet_get_data"),
            packet_get_length: symbol!(b"enet_packet_get_length"),
            packet_dispose: symbol!(b"enet_packet_dispose"),
            peer_send: symbol!(b"enet_peer_send"),
            peer_get_mtu: symbol!(b"enet_peer_get_mtu"),
            peer_disconnect: symbol!(b"enet_peer_disconnect"),
        };
        let initialize: unsafe extern "C" fn() -> c_int = symbol!(b"enet_initialize");
        // SAFETY: `enet_initialize` takes no arguments; the library stays loaded.
        assert_eq!(unsafe { initialize() }, 0, "enet_initialize failed");
        csharp
    })
}

/// Which library produced an event.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Side {
    Glue,
    Csharp,
}

/// One event as a test sees it, with any packet copied out and disposed by its own library.
#[derive(Debug, Clone)]
struct Seen {
    side: Side,
    kind: i32,
    peer: *mut c_void,
    channel: u8,
    data: u32,
    bytes: Option<Vec<u8>>,
}

/// A glue server host and an ENet-CSharp client host, with the peer handle each side holds.
struct Link {
    server: *mut GlueHost,
    client: *mut c_void,
    server_peer: *mut PeerRecord,
    client_peer: *mut c_void,
}

impl Drop for Link {
    fn drop(&mut self) {
        // SAFETY: each host is live, belongs to its own library and is destroyed once, here.
        unsafe {
            enet_host_destroy(self.server);
            (csharp().host_destroy)(self.client);
        }
    }
}

/// Fills a 20-byte ENet-CSharp address with an IP literal and a port.
fn csharp_address(ip: &str, port: u16) -> [u8; ADDRESS_BYTES] {
    let mut address = [0u8; ADDRESS_BYTES];
    let text = CString::new(ip).unwrap();
    // SAFETY: the buffer holds 20 bytes, at least ENet-CSharp's address size, and `text` is
    // NUL-terminated.
    assert_eq!(
        unsafe { (csharp().address_set_ip)(address.as_mut_ptr(), text.as_ptr()) },
        0
    );
    address[ADDRESS_PORT_OFFSET..ADDRESS_PORT_OFFSET + 2].copy_from_slice(&port.to_ne_bytes());
    address
}

/// Copies a glue packet out and disposes it.
///
/// # Safety
/// `packet` must be a live handle from the glue that the caller owns.
unsafe fn take_glue_packet(packet: *mut enet::PacketHandle) -> Vec<u8> {
    let length = enet_packet_get_length(packet) as usize;
    let bytes = slice::from_raw_parts(enet_packet_get_data(packet), length).to_vec();
    enet_packet_dispose(packet);
    bytes
}

/// Copies an ENet-CSharp packet out and disposes it.
///
/// # Safety
/// `packet` must be a live handle from ENet-CSharp that the caller owns.
unsafe fn take_csharp_packet(packet: *mut c_void) -> Vec<u8> {
    let csharp = csharp();
    let length = (csharp.packet_get_length)(packet) as usize;
    let bytes = slice::from_raw_parts((csharp.packet_get_data)(packet), length).to_vec();
    (csharp.packet_dispose)(packet);
    bytes
}

/// Services both hosts in turn until `pred` accepts an event; fails the test, naming
/// `waiting_for`, when it has not after `DEADLINE_MS`.
fn pump(link: &Link, waiting_for: &str, mut pred: impl FnMut(&Seen) -> bool) {
    let deadline = Instant::now() + Duration::from_millis(DEADLINE_MS);
    while Instant::now() < deadline {
        let mut event = empty_event();
        // SAFETY: the server is live and `event` is valid for writes.
        let result = unsafe { enet_host_service(link.server, &mut event, 1) };
        assert!(result >= 0, "glue service failed");
        if result == 1 {
            let bytes = (!event.packet.is_null()).then(|| {
                // SAFETY: a received glue packet is live and owned here.
                unsafe { take_glue_packet(event.packet) }
            });
            let seen = Seen {
                side: Side::Glue,
                kind: event.kind,
                peer: event.peer.cast(),
                channel: event.channel_id,
                data: event.data,
                bytes,
            };
            if pred(&seen) {
                return;
            }
        }

        let mut event = CEvent {
            kind: 0,
            peer: ptr::null_mut(),
            channel: 0,
            data: 0,
            packet: ptr::null_mut(),
        };
        // SAFETY: the client is live and `event` matches ENet-CSharp's `ENetEvent` layout.
        let result = unsafe { (csharp().host_service)(link.client, &mut event, 1) };
        assert!(result >= 0, "ENet-CSharp service failed");
        if result == 1 {
            let bytes = (!event.packet.is_null()).then(|| {
                // SAFETY: a received ENet-CSharp packet is live and owned here.
                unsafe { take_csharp_packet(event.packet) }
            });
            let seen = Seen {
                side: Side::Csharp,
                kind: event.kind,
                peer: event.peer,
                channel: event.channel,
                data: event.data,
                bytes,
            };
            if pred(&seen) {
                return;
            }
        }
    }
    panic!("timed out after {DEADLINE_MS} ms waiting for {waiting_for}");
}

/// Connects an ENet-CSharp client to a glue server and waits for both connect events.
fn link() -> Link {
    let csharp = csharp();
    let (server, port) = server("127.0.0.1");
    let address = csharp_address("127.0.0.1", port);
    // SAFETY: a null address is allowed for a client host.
    let client = unsafe { (csharp.host_create)(ptr::null(), 1, CHANNELS, 0, 0, 0) };
    assert!(!client.is_null(), "ENet-CSharp client could not be created");
    // SAFETY: `client` is live and `address` is a valid 20-byte ENet-CSharp address.
    let client_peer = unsafe { (csharp.host_connect)(client, address.as_ptr(), CHANNELS, 0) };
    assert!(!client_peer.is_null(), "ENet-CSharp connect failed");

    let mut link = Link {
        server,
        client,
        server_peer: ptr::null_mut(),
        client_peer,
    };
    let mut server_peer = None;
    let mut client_up = false;
    pump(&link, "both connect events", |seen| {
        match (seen.side, seen.kind) {
            (Side::Glue, CONNECT) => server_peer = Some(seen.peer),
            (Side::Csharp, CONNECT) => client_up = true,
            _ => {}
        }
        server_peer.is_some() && client_up
    });
    link.server_peer = server_peer.unwrap().cast();
    link
}

fn payload(length: usize) -> Vec<u8> {
    (0..length).map(|index| (index % 251) as u8).collect()
}

/// Sends `length` bytes from the ENet-CSharp client with `flags` on `channel`.
fn csharp_sends(link: &Link, length: usize, channel: u8, flags: u32) {
    let csharp = csharp();
    let bytes = payload(length);
    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { (csharp.packet_create)(bytes.as_ptr(), bytes.len(), flags) };
    assert!(!packet.is_null(), "ENet-CSharp packet could not be created");
    // SAFETY: the peer is live and the packet is owned by ENet-CSharp after a 0 return.
    assert_eq!(
        unsafe { (csharp.peer_send)(link.client_peer, channel, packet) },
        0
    );
}

/// Sends `length` bytes from the glue server with `flags` on `channel`.
fn glue_sends(link: &Link, length: usize, channel: u8, flags: u32) {
    let bytes = payload(length);
    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create(bytes.as_ptr(), bytes.len(), flags) };
    // SAFETY: the peer is live and the glue owns the packet after a 0 return.
    assert_eq!(
        unsafe { enet_peer_send(link.server_peer, channel, packet) },
        0
    );
}

/// Waits for `count` receive events on `side`, returning them in arrival order.
fn receive(link: &Link, side: Side, count: usize) -> Vec<Seen> {
    let mut received = Vec::new();
    pump(
        link,
        &format!("{count} packets on the {side:?} side"),
        |seen| {
            if seen.side == side && seen.kind == RECEIVE {
                received.push(seen.clone());
            }
            received.len() == count
        },
    );
    received
}

#[test]
fn fixture_hashes_match() {
    assert_eq!(
        sha256_hex(&fixture_path("linux-x64/native/libenet.so")),
        LINUX_LIBRARY_SHA256
    );
    assert_eq!(
        sha256_hex(&fixture_path("win-x64/native/enet.dll")),
        WINDOWS_LIBRARY_SHA256
    );
}

#[test]
fn csharp_client_connects_and_is_granted_1280() {
    let _csharp = csharp_lock();
    let link = link();

    // SAFETY: each peer is live and belongs to the library it is passed to.
    let glue_mtu = unsafe { enet_peer_get_mtu(link.server_peer) };
    // SAFETY: as above.
    let csharp_mtu = unsafe { (csharp().peer_get_mtu)(link.client_peer) };
    assert_eq!(glue_mtu, 1280);
    assert_eq!(csharp_mtu, 1280);
}

#[test]
fn csharp_client_packets_arrive_on_all_three_channels() {
    let _csharp = csharp_lock();
    let link = link();

    csharp_sends(&link, 32, 0, RELIABLE);
    csharp_sends(&link, 32, 1, UNTHROTTLED);
    csharp_sends(&link, 32, 2, UNSEQUENCED);

    assert_three_channels(receive(&link, Side::Glue, 3));
}

#[test]
fn csharp_client_3000_byte_reliable_arrives() {
    let _csharp = csharp_lock();
    let link = link();

    csharp_sends(&link, 3000, 0, RELIABLE);

    let received = receive(&link, Side::Glue, 1);
    assert_eq!(received[0].channel, 0);
    assert_eq!(received[0].bytes.as_deref(), Some(payload(3000).as_slice()));
}

#[test]
fn glue_host_3000_byte_reliable_reaches_csharp_client() {
    let _csharp = csharp_lock();
    let link = link();

    glue_sends(&link, 3000, 0, RELIABLE);

    let received = receive(&link, Side::Csharp, 1);
    assert_eq!(received[0].channel, 0);
    assert_eq!(received[0].bytes.as_deref(), Some(payload(3000).as_slice()));
}

#[test]
fn glue_host_packets_reach_csharp_client_on_all_three_channels() {
    let _csharp = csharp_lock();
    let link = link();

    glue_sends(&link, 32, 0, RELIABLE);
    glue_sends(&link, 32, 1, UNTHROTTLED);
    glue_sends(&link, 32, 2, UNSEQUENCED);

    assert_three_channels(receive(&link, Side::Csharp, 3));
}

#[test]
fn disconnect_reasons_cross_both_ways() {
    let _csharp = csharp_lock();
    let first = link();
    // SAFETY: the server peer is live and belongs to the glue.
    unsafe { enet_peer_disconnect(first.server_peer, 16) };
    let mut client_reason = None;
    pump(
        &first,
        "the ENet-CSharp client's disconnect event",
        |seen| {
            if seen.side == Side::Csharp && seen.kind == DISCONNECT {
                client_reason = Some(seen.data);
            }
            client_reason.is_some()
        },
    );
    assert_eq!(client_reason, Some(16));
    drop(first);

    let second = link();
    // SAFETY: the client peer is live and belongs to ENet-CSharp.
    unsafe { (csharp().peer_disconnect)(second.client_peer, 5) };
    let mut server_reason = None;
    pump(&second, "the glue server's disconnect event", |seen| {
        if seen.side == Side::Glue && seen.kind == DISCONNECT {
            server_reason = Some(seen.data);
        }
        server_reason.is_some()
    });
    assert_eq!(server_reason, Some(5));
}

/// Checks one 32-byte packet per channel arrived, whatever the order across channels.
fn assert_three_channels(received: Vec<Seen>) {
    let mut channels: Vec<u8> = received.iter().map(|seen| seen.channel).collect();
    channels.sort_unstable();
    assert_eq!(channels, [0, 1, 2]);
    for seen in &received {
        assert_eq!(seen.bytes.as_deref(), Some(payload(32).as_slice()));
    }
}
