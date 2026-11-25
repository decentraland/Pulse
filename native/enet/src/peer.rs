use std::os::raw::{c_char, c_int};

use rusty_enet::{Peer, PeerID, PeerState};

use crate::address::{ip_text, write_ip};
use crate::guard::guard;
use crate::host::GlueHost;
use crate::packet::PacketHandle;
use crate::socket::GlueSocket;

/// A peer handle: one per host slot, valid until `enet_host_destroy`.
///
/// Queries report the slot's current state, so a handle stays usable after its peer disconnects.
#[repr(C)]
pub struct PeerRecord {
    host: *mut GlueHost,
    index: u32,
}

impl PeerRecord {
    pub(crate) fn new(host: *mut GlueHost, index: u32) -> PeerRecord {
        PeerRecord { host, index }
    }

    /// Returns the slot's peer.
    ///
    /// # Safety
    ///
    /// `record` must be null or a handle of a live host, and no other reference to that host may
    /// be in use.
    unsafe fn peer<'a>(record: *const PeerRecord) -> Option<&'a Peer<GlueSocket>> {
        // SAFETY: the caller guarantees `record` is null or a live handle.
        let record = unsafe { record.as_ref() }?;
        // SAFETY: a live handle points at its live host.
        let glue = unsafe { record.host.as_ref() }?;
        glue.host.get_peer(PeerID(record.index as usize))
    }

    /// Returns the slot's peer for mutation.
    ///
    /// # Safety
    ///
    /// As for [`PeerRecord::peer`].
    unsafe fn peer_mut<'a>(record: *const PeerRecord) -> Option<&'a mut Peer<GlueSocket>> {
        // SAFETY: the caller guarantees the contract of `slot`.
        let (glue, id) = unsafe { PeerRecord::slot(record) }?;
        glue.host.get_peer_mut(id)
    }

    /// Returns the slot's host for mutation and the slot's peer ID.
    ///
    /// # Safety
    ///
    /// As for [`PeerRecord::peer`].
    unsafe fn slot<'a>(record: *const PeerRecord) -> Option<(&'a mut GlueHost, PeerID)> {
        // SAFETY: the caller guarantees `record` is null or a live handle.
        let record = unsafe { record.as_ref() }?;
        // SAFETY: a live handle points at its live host, which nothing else references.
        let glue = unsafe { record.host.as_mut() }?;
        Some((glue, PeerID(record.index as usize)))
    }
}

/// Returns the peer's slot index, or 0 for a null handle.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_get_id(peer: *const PeerRecord) -> u32 {
    guard(0, || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        Some(unsafe { peer.as_ref() }?.index)
    })
}

/// Writes the peer's IP as a NUL-terminated string: dotted for IPv4 and mapped IPv4, IPv6 text
/// otherwise. Returns -1 when the peer has no address or `ip_length` cannot hold the text and
/// its terminator.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host, and `ip` null or valid for `ip_length` bytes
/// of writes.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_get_ip(
    peer: *const PeerRecord,
    ip: *mut c_char,
    ip_length: usize,
) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        let address = unsafe { PeerRecord::peer(peer) }?.address()?;
        // SAFETY: the caller guarantees `ip` is null or valid for `ip_length` bytes of writes.
        unsafe { write_ip(&ip_text(address), ip, ip_length) }
    })
}

/// Returns the peer's port, or 0 when it has no address or the handle is null.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_get_port(peer: *const PeerRecord) -> u16 {
    guard(0, || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        Some(unsafe { PeerRecord::peer(peer) }?.address()?.port())
    })
}

/// Returns the peer's MTU, or 0 for a null handle.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_get_mtu(peer: *const PeerRecord) -> u32 {
    guard(0, || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        Some(u32::from(unsafe { PeerRecord::peer(peer) }?.mtu()))
    })
}

/// Returns the peer's state, 0 disconnected through 9 zombie, or -1 for a null handle.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_get_state(peer: *const PeerRecord) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        Some(state_code(unsafe { PeerRecord::peer(peer) }?.state()))
    })
}

/// Returns the peer's mean round-trip time in milliseconds, or 0 for a null handle.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_get_rtt(peer: *const PeerRecord) -> u32 {
    guard(0, || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        let rtt = unsafe { PeerRecord::peer(peer) }?.round_trip_time();
        Some(u32::try_from(rtt.as_millis()).unwrap_or(u32::MAX))
    })
}

/// Sets the peer's timeout limit, minimum and maximum; 0 keeps ENet's default for that value.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_timeout(
    peer: *mut PeerRecord,
    limit: u32,
    minimum: u32,
    maximum: u32,
) {
    guard((), || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        unsafe { PeerRecord::peer_mut(peer) }?.set_timeout(limit, minimum, maximum);
        Some(())
    });
}

/// Sets the interval between pings to the peer, in milliseconds.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_ping_interval(peer: *mut PeerRecord, interval: u32) {
    guard((), || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        unsafe { PeerRecord::peer_mut(peer) }?.set_ping_interval(interval);
        Some(())
    });
}

/// Queues `packet` on channel `channel_id`, and flushes the host at once for an `Instant` packet.
///
/// Returns 0 and consumes the handle on success: the caller must not use or dispose it after.
/// Returns -1 and leaves the handle with the caller when either handle is null, the peer is not
/// connected, the channel does not exist or the packet cannot be queued.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host, and `packet` null or a live handle owned by
/// the caller.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_send(
    peer: *mut PeerRecord,
    channel_id: u8,
    packet: *mut PacketHandle,
) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees `packet` is null or a live handle.
        let handle = unsafe { packet.as_ref() }?;
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        let (glue, id) = unsafe { PeerRecord::slot(peer) }?;
        glue.host
            .get_peer_mut(id)?
            .send(channel_id, &handle.packet)
            .ok()?;
        // Flushing before the handle is consumed keeps it with the caller if the flush panics.
        if handle.instant {
            glue.host.flush();
        }
        // SAFETY: `packet` came from `Box::into_raw` and the caller hands it over on success;
        // `rusty_enet` keeps its own reference to the queued packet.
        drop(unsafe { Box::from_raw(packet) });
        Some(0)
    })
}

/// Requests a graceful disconnect that sends `data` to the peer; both sides then report a
/// disconnect event.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_disconnect(peer: *mut PeerRecord, data: u32) {
    guard((), || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        unsafe { PeerRecord::peer_mut(peer) }?.disconnect(data);
        Some(())
    });
}

/// Disconnects at once, sending `data` to the peer without waiting for it. No local disconnect
/// event follows.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_disconnect_now(peer: *mut PeerRecord, data: u32) {
    guard((), || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        unsafe { PeerRecord::peer_mut(peer) }?.disconnect_now(data);
        Some(())
    });
}

/// Disconnects with `data` once every queued outgoing packet has been sent.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_disconnect_later(peer: *mut PeerRecord, data: u32) {
    guard((), || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        unsafe { PeerRecord::peer_mut(peer) }?.disconnect_later(data);
        Some(())
    });
}

/// Resets the peer's slot without notifying the peer or raising a local event.
///
/// # Safety
///
/// `peer` must be null or a handle of a live host.
#[no_mangle]
pub unsafe extern "C" fn enet_peer_reset(peer: *mut PeerRecord) {
    guard((), || {
        // SAFETY: the caller guarantees `peer` is null or a live handle.
        unsafe { PeerRecord::peer_mut(peer) }?.reset();
        Some(())
    });
}

/// Maps a state to ENet-CSharp's `PeerState` value.
fn state_code(state: PeerState) -> c_int {
    match state {
        PeerState::Disconnected => 0,
        PeerState::Connecting => 1,
        PeerState::AcknowledgingConnect => 2,
        PeerState::ConnectionPending => 3,
        PeerState::ConnectionSucceeded => 4,
        PeerState::Connected => 5,
        PeerState::DisconnectLater => 6,
        PeerState::Disconnecting => 7,
        PeerState::AcknowledgingDisconnect => 8,
        PeerState::Zombie => 9,
    }
}
