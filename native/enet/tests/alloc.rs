//! Packet handles do not leak: live heap allocations stay flat across thousands of packet cycles.
//!
//! The counting allocator sees the whole process, so the tests in this file hold one lock.

mod common;

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicIsize, Ordering};
use std::sync::Mutex;

use common::{address, client, empty_event, pump_for, pump_until, server};
use enet::{
    enet_host_connect, enet_host_destroy, enet_host_flush, enet_host_service, enet_packet_create,
    enet_packet_dispose, enet_peer_send, GlueHost, PeerRecord,
};

const CONNECT: i32 = 1;
const RECEIVE: i32 = 3;
const RELIABLE: u32 = 1;
const CHANNELS: usize = 3;
/// A channel the connection does not have, so a send on it fails.
const MISSING_CHANNEL: u8 = 9;
const DEADLINE_MS: u64 = 3000;
const WARM_UP_CYCLES: usize = 200;
const MEASURED_CYCLES: usize = 2000;
/// Growth allowed over the measured cycles. A per-cycle leak adds at least one allocation per
/// cycle (2000 in all); amortised growth of a `Vec` or queue inside the hosts adds a handful.
const ALLOWED_GROWTH: isize = 32;
/// Time both hosts are serviced before a measurement, so every acknowledgement has arrived.
const SETTLE_MS: u64 = 200;

/// Counts live allocations and bytes on top of the system allocator.
struct Counting;

static LIVE_ALLOCATIONS: AtomicIsize = AtomicIsize::new(0);
static LIVE_BYTES: AtomicIsize = AtomicIsize::new(0);

// SAFETY: every call is forwarded to `System` unchanged; the counters do not affect allocation.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        // SAFETY: forwarded with the caller's layout.
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() {
            LIVE_ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
            LIVE_BYTES.fetch_add(layout.size() as isize, Ordering::Relaxed);
        }
        pointer
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        // SAFETY: forwarded with the caller's layout.
        let pointer = unsafe { System.alloc_zeroed(layout) };
        if !pointer.is_null() {
            LIVE_ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
            LIVE_BYTES.fetch_add(layout.size() as isize, Ordering::Relaxed);
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        // SAFETY: forwarded with the caller's pointer and layout.
        unsafe { System.dealloc(pointer, layout) };
        LIVE_ALLOCATIONS.fetch_sub(1, Ordering::Relaxed);
        LIVE_BYTES.fetch_sub(layout.size() as isize, Ordering::Relaxed);
    }

    unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        // SAFETY: forwarded with the caller's pointer, layout and size.
        let moved = unsafe { System.realloc(pointer, layout, new_size) };
        if !moved.is_null() {
            LIVE_BYTES.fetch_add(
                new_size as isize - layout.size() as isize,
                Ordering::Relaxed,
            );
        }
        moved
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

/// Serialises the tests: the counters are process-wide.
static SERIAL: Mutex<()> = Mutex::new(());

/// Live allocation count and bytes.
fn live() -> (isize, isize) {
    (
        LIVE_ALLOCATIONS.load(Ordering::Relaxed),
        LIVE_BYTES.load(Ordering::Relaxed),
    )
}

/// A connected listening host and client.
struct Pair {
    server: *mut GlueHost,
    client: *mut GlueHost,
    client_peer: *mut PeerRecord,
}

impl Pair {
    fn connect() -> Pair {
        let (server, port) = server("127.0.0.1");
        let client = client();
        let target = address("127.0.0.1", port);
        // SAFETY: `client` is live and `target` is valid for the call.
        let client_peer = unsafe { enet_host_connect(client, &target, CHANNELS, 0) };
        assert!(!client_peer.is_null());
        let mut server_up = false;
        let mut client_up = false;
        let done = pump_until(&[server, client], DEADLINE_MS, |host, event| {
            if event.kind == CONNECT {
                if host == server {
                    server_up = true;
                } else {
                    client_up = true;
                }
            }
            server_up && client_up
        });
        assert!(done, "connection did not complete");
        Pair {
            server,
            client,
            client_peer,
        }
    }

    fn settle(&self) {
        pump_for(&[self.server, self.client], SETTLE_MS);
    }

    fn destroy(self) {
        // SAFETY: both hosts are live and destroyed once.
        unsafe {
            enet_host_destroy(self.server);
            enet_host_destroy(self.client);
        }
    }
}

/// Creates a reliable packet, sends it, waits until the server receives it, disposes the
/// received packet and lets the client read the acknowledgement.
fn delivered_cycle(pair: &Pair, bytes: &[u8]) {
    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create(bytes.as_ptr(), bytes.len(), RELIABLE) };
    assert!(!packet.is_null());
    // SAFETY: `client_peer` is a live record and `packet` a live handle the caller owns.
    assert_eq!(unsafe { enet_peer_send(pair.client_peer, 0, packet) }, 0);
    // SAFETY: `client` is live.
    unsafe { enet_host_flush(pair.client) };
    let received = pump_until(&[pair.server], DEADLINE_MS, |_, event| {
        assert_eq!(event.kind, RECEIVE, "unexpected server event");
        // SAFETY: a received packet handle is owned by the caller and disposed once.
        unsafe { enet_packet_dispose(event.packet) };
        true
    });
    assert!(received, "the packet was not delivered");
    let mut event = empty_event();
    // SAFETY: `client` is live and `event` is valid for writes.
    assert_eq!(unsafe { enet_host_service(pair.client, &mut event, 0) }, 0);
}

/// Creates a reliable packet, fails to send it on a channel that does not exist and disposes it.
fn failed_send_cycle(pair: &Pair, bytes: &[u8]) {
    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create(bytes.as_ptr(), bytes.len(), RELIABLE) };
    assert!(!packet.is_null());
    // SAFETY: `client_peer` is a live record and `packet` a live handle the caller owns.
    assert_eq!(
        unsafe { enet_peer_send(pair.client_peer, MISSING_CHANNEL, packet) },
        -1
    );
    // SAFETY: a failed send leaves the handle with the caller; disposed once.
    unsafe { enet_packet_dispose(packet) };
}

/// Runs `cycle` for a warm-up and then for the measured count, and asserts live allocations did
/// not grow with the number of cycles.
fn assert_flat(pair: &Pair, cycle: impl Fn(&Pair, &[u8])) {
    let bytes = vec![0x5A; 200];
    for _ in 0..WARM_UP_CYCLES {
        cycle(pair, &bytes);
    }
    pair.settle();
    let (count_before, bytes_before) = live();

    for _ in 0..MEASURED_CYCLES {
        cycle(pair, &bytes);
    }
    pair.settle();
    let (count_after, bytes_after) = live();

    let growth = count_after - count_before;
    println!(
        "live allocations {count_before} -> {count_after}, bytes {bytes_before} -> {bytes_after}"
    );
    assert!(
        growth <= ALLOWED_GROWTH,
        "live allocations grew by {growth} over {MEASURED_CYCLES} cycles \
         ({bytes_before} -> {bytes_after} bytes)"
    );
}

#[test]
fn send_receive_dispose_cycles_do_not_grow_the_heap() {
    let _serial = SERIAL
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let pair = Pair::connect();

    assert_flat(&pair, delivered_cycle);

    pair.destroy();
}

#[test]
fn failed_send_then_dispose_does_not_leak() {
    let _serial = SERIAL
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let pair = Pair::connect();

    assert_flat(&pair, failed_send_cycle);

    pair.destroy();
}
