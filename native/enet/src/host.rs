use std::fmt;
use std::io;
use std::net::SocketAddr;
use std::os::raw::c_int;
use std::panic::{self, AssertUnwindSafe};
use std::ptr;
use std::time::{Duration, Instant};

use rusty_enet::{Event, Host, HostSettings, ReceiveLimitKind, ReceivePolicy};

use crate::address::{to_socket_addr, ENetAddress};
use crate::guard::guard;
use crate::log::LogLimiter;
use crate::packet::PacketHandle;
use crate::peer::PeerRecord;
use crate::socket::GlueSocket;

/// `ENetEvent.kind`: no event.
const EVENT_NONE: c_int = 0;
/// `ENetEvent.kind`: a peer connected.
const EVENT_CONNECT: c_int = 1;
/// `ENetEvent.kind`: a peer disconnected or timed out.
const EVENT_DISCONNECT: c_int = 2;
/// `ENetEvent.kind`: a packet arrived.
const EVENT_RECEIVE: c_int = 3;

// A listening host's receive limits. They mirror the receive policy of dcl-one's Pulse server.
/// Largest incoming packet, reassembled, a listening host accepts; a larger declaration
/// disconnects the peer before anything is allocated for it.
const RECEIVE_MAXIMUM_PACKET_SIZE: usize = 8192;
/// Most incoming bytes a listening host queues per peer.
const RECEIVE_MAXIMUM_QUEUED_BYTES: usize = 65536;
/// Most incoming commands a listening host queues per peer.
const RECEIVE_MAXIMUM_QUEUED_COMMANDS: usize = 256;

/// Disconnect data sent to a peer refused by the receive limit: `DisconnectReason.PACKET_CORRUPTED`
/// in `src/DCLPulse.Transport/Package/Runtime/DisconnectReason.cs`.
const DISCONNECT_PACKET_CORRUPTED: u32 = 16;

/// A host handle: the `rusty_enet` host, one peer record per slot and the host's stderr limiter.
///
/// Records live until `enet_host_destroy`, so peer handles stay valid memory after a peer
/// disconnects. One thread drives a host.
pub struct GlueHost {
    pub(crate) host: Host<GlueSocket>,
    peers: Box<[PeerRecord]>,
    log: LogLimiter,
}

/// ENet-CSharp's event: kind 0 none, 1 connect, 2 disconnect, 3 receive.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct ENetEvent {
    pub kind: c_int,
    pub peer: *mut PeerRecord,
    pub channel_id: u8,
    pub data: u32,
    pub packet: *mut PacketHandle,
}

impl GlueHost {
    /// Returns the address the host's socket is bound to.
    #[doc(hidden)]
    pub fn local_addr(&self) -> io::Result<SocketAddr> {
        self.host.socket().local_addr()
    }

    /// Returns the handle of slot `index`, or null when the slot does not exist.
    fn record(&self, index: usize) -> *mut PeerRecord {
        record(&self.peers, index)
    }
}

/// Returns the handle of slot `index` in `peers`, or null when the slot does not exist.
///
/// The pointer comes from a shared borrow: records are never written after creation.
fn record(peers: &[PeerRecord], index: usize) -> *mut PeerRecord {
    match peers.get(index) {
        Some(record) => ptr::from_ref(record).cast_mut(),
        None => ptr::null_mut(),
    }
}

/// Writes `event` into `out`, boxing a received packet into a handle the caller owns. Returns
/// false for events the glue drops.
///
/// A packet-size refusal is terminal: the peer is disconnected at once with
/// `PACKET_CORRUPTED` and the caller gets a disconnect event for it. The other receive limits
/// only refuse one command and are dropped.
fn store(
    peers: &[PeerRecord],
    log: &mut LogLimiter,
    event: Event<'_, GlueSocket>,
    out: &mut ENetEvent,
) -> bool {
    let (kind, peer, channel_id, data, packet) = match event {
        Event::Connect { peer, data } => (EVENT_CONNECT, peer.id(), 0, data, ptr::null_mut()),
        Event::Disconnect { peer, data } => (EVENT_DISCONNECT, peer.id(), 0, data, ptr::null_mut()),
        Event::Receive {
            peer,
            channel_id,
            packet,
        } => {
            let handle = Box::into_raw(Box::new(PacketHandle::received(packet)));
            (EVENT_RECEIVE, peer.id(), channel_id, 0, handle)
        }
        Event::ReceiveLimitExceeded {
            peer,
            kind: ReceiveLimitKind::PacketSize,
            ..
        } => {
            peer.disconnect_now(DISCONNECT_PACKET_CORRUPTED);
            let id = peer.id();
            report(
                log,
                format_args!(
                    "peer {} declared a packet above {RECEIVE_MAXIMUM_PACKET_SIZE} bytes and was \
                     disconnected",
                    id.0
                ),
            );
            (
                EVENT_DISCONNECT,
                id,
                0,
                DISCONNECT_PACKET_CORRUPTED,
                ptr::null_mut(),
            )
        }
        Event::ReceiveLimitExceeded { .. } => return false,
    };
    *out = ENetEvent {
        kind,
        peer: record(peers, peer.0),
        channel_id,
        data,
        packet,
    };
    true
}

/// Writes `message` to stderr with the `enet:` prefix when `log` lets a line through.
fn report(log: &mut LogLimiter, message: fmt::Arguments<'_>) {
    if let Some(suppressed) = log.admit(Instant::now()) {
        // `eprintln!` panics when stderr cannot be written. Diagnostics must never fail a host
        // call or lose the event being stored, so that panic stops here.
        let _ = panic::catch_unwind(AssertUnwindSafe(|| {
            eprintln!("enet: {message} ({suppressed} lines suppressed since the previous one)");
        }));
    }
}

/// Returns ENet's clock for one host: monotonic, so a wall-clock step cannot time peers out,
/// and never 0, which ENet treats as "unset".
fn monotonic_clock() -> impl Fn() -> Duration {
    let epoch = Instant::now();
    move || epoch.elapsed() + Duration::from_millis(1)
}

fn clear(event: &mut ENetEvent) {
    *event = ENetEvent {
        kind: EVENT_NONE,
        peer: ptr::null_mut(),
        channel_id: 0,
        data: 0,
        packet: ptr::null_mut(),
    };
}

/// Creates a host bound to `address`, or to an ephemeral dual-stack port when it is null.
/// Returns null on failure. `channel_limit` 0 means the maximum, bandwidth 0 means unlimited,
/// and `buffer_size` is ignored.
///
/// A host created with an address listens, and refuses packets above 8192 bytes before
/// reassembly; a client host (null address) has no receive limit.
///
/// # Safety
///
/// `address` must be null or valid for reads.
#[no_mangle]
pub unsafe extern "C" fn enet_host_create(
    address: *const ENetAddress,
    peer_count: usize,
    channel_limit: usize,
    incoming_bandwidth: u32,
    outgoing_bandwidth: u32,
    _buffer_size: c_int,
) -> *mut GlueHost {
    guard(ptr::null_mut(), || {
        // SAFETY: the caller guarantees `address` is null or valid for reads.
        let bind = unsafe { address.as_ref() }.map(to_socket_addr);
        let socket = GlueSocket::bind(bind).ok()?;
        let defaults = HostSettings::default();
        let settings = HostSettings {
            receive_policy: bind.map(|_| ReceivePolicy {
                maximum_packet_size: RECEIVE_MAXIMUM_PACKET_SIZE,
                maximum_queued_bytes: RECEIVE_MAXIMUM_QUEUED_BYTES,
                maximum_queued_commands: RECEIVE_MAXIMUM_QUEUED_COMMANDS,
            }),
            time: Box::new(monotonic_clock()),
            peer_limit: peer_count,
            channel_limit: if channel_limit == 0 {
                defaults.channel_limit
            } else {
                channel_limit
            },
            incoming_bandwidth_limit: (incoming_bandwidth != 0).then_some(incoming_bandwidth),
            outgoing_bandwidth_limit: (outgoing_bandwidth != 0).then_some(outgoing_bandwidth),
            ..defaults
        };
        let host = Host::new(socket, settings).ok()?;
        let glue = Box::into_raw(Box::new(GlueHost {
            host,
            peers: Box::new([]),
            log: LogLimiter::new(),
        }));
        // SAFETY: `glue` was just allocated and nothing else refers to it yet.
        let slots = unsafe { (*glue).host.peer_limit() };
        let peers = (0..slots)
            .map(|index| PeerRecord::new(glue, index as u32))
            .collect();
        // SAFETY: as above.
        unsafe { (*glue).peers = peers };
        Some(glue)
    })
}

/// Destroys a host and its peer records. Null is a no-op.
///
/// # Safety
///
/// `host` must be null or a live host from `enet_host_create`; it and its peer handles are
/// invalid afterwards.
#[no_mangle]
pub unsafe extern "C" fn enet_host_destroy(host: *mut GlueHost) {
    guard((), || {
        if !host.is_null() {
            // SAFETY: the caller guarantees `host` came from `Box::into_raw` and is live.
            drop(unsafe { Box::from_raw(host) });
        }
        Some(())
    });
}

/// Services the host for up to `timeout` milliseconds. Returns 1 with an event, 0 on timeout,
/// and -1 on a socket error or null handle. A socket error is also written to stderr, at most
/// one line per host every 5 seconds.
///
/// # Safety
///
/// `host` must be null or a live host, and `event` null or valid for writes.
#[no_mangle]
pub unsafe extern "C" fn enet_host_service(
    host: *mut GlueHost,
    event: *mut ENetEvent,
    timeout: u32,
) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees validity; null is rejected by `as_mut`.
        let glue = unsafe { host.as_mut() }?;
        // SAFETY: as above.
        let out = unsafe { event.as_mut() }?;
        clear(out);
        let deadline = Instant::now() + Duration::from_millis(u64::from(timeout));
        loop {
            let dropped = match glue.host.service() {
                Err(err) => {
                    report(&mut glue.log, format_args!("host service failed: {err}"));
                    return Some(-1);
                }
                Ok(Some(next)) => {
                    if store(&glue.peers, &mut glue.log, next, out) {
                        return Some(1);
                    }
                    true
                }
                Ok(None) => false,
            };
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Some(0);
            }
            // A wait that ends without a datagram at the deadline is the timeout: return without
            // another pass, as ENet does.
            if !dropped
                && !glue.host.socket().wait_readable(remaining)
                && Instant::now() >= deadline
            {
                return Some(0);
            }
        }
    })
}

/// Dispatches one already queued event without touching the socket. Returns 1 with an event,
/// 0 when none is queued, and -1 on a null handle.
///
/// # Safety
///
/// `host` must be null or a live host, and `event` null or valid for writes.
#[no_mangle]
pub unsafe extern "C" fn enet_host_check_events(
    host: *mut GlueHost,
    event: *mut ENetEvent,
) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees validity; null is rejected by `as_mut`.
        let glue = unsafe { host.as_mut() }?;
        // SAFETY: as above.
        let out = unsafe { event.as_mut() }?;
        clear(out);
        while let Some(next) = glue.host.check_events() {
            if store(&glue.peers, &mut glue.log, next, out) {
                return Some(1);
            }
        }
        Some(0)
    })
}

/// Sends every queued packet now.
///
/// # Safety
///
/// `host` must be null or a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_host_flush(host: *mut GlueHost) {
    guard((), || {
        // SAFETY: the caller guarantees validity; null is rejected by `as_mut`.
        unsafe { host.as_mut() }?.host.flush();
        Some(())
    });
}

/// Starts a connection to `address` and returns its peer handle, or null on failure.
/// `channel_count` 0 means the host's channel limit.
///
/// # Safety
///
/// `host` must be null or a live host, and `address` null or valid for reads.
#[no_mangle]
pub unsafe extern "C" fn enet_host_connect(
    host: *mut GlueHost,
    address: *const ENetAddress,
    channel_count: usize,
    data: u32,
) -> *mut PeerRecord {
    guard(ptr::null_mut(), || {
        // SAFETY: the caller guarantees validity; null is rejected by `as_mut`.
        let glue = unsafe { host.as_mut() }?;
        // SAFETY: as above.
        let address = to_socket_addr(unsafe { address.as_ref() }?);
        let channels = if channel_count == 0 {
            glue.host.channel_limit()
        } else {
            channel_count
        };
        let index = glue.host.connect(address, channels, data).ok()?.id().0;
        Some(glue.record(index))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn monotonic_clock_starts_at_one_millisecond_and_never_decreases() {
        let clock = monotonic_clock();
        let mut previous = clock();
        assert!(previous >= Duration::from_millis(1));
        for _ in 0..10_000 {
            let now = clock();
            assert!(now >= previous);
            previous = now;
        }
    }
}
