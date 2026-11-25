use std::os::raw::{c_char, c_int};

use rusty_enet::{Peer, PeerID, PeerState};

use crate::address::ip_text;
use crate::guard::guard;
use crate::host::GlueHost;
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
        // SAFETY: the caller guarantees `record` is null or a live handle.
        let record = unsafe { record.as_ref() }?;
        // SAFETY: a live handle points at its live host, which nothing else references.
        let glue = unsafe { record.host.as_mut() }?;
        glue.host.get_peer_mut(PeerID(record.index as usize))
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
        if ip.is_null() {
            return None;
        }
        let text = ip_text(address);
        let bytes = text.as_bytes();
        if bytes.len() >= ip_length {
            return None;
        }
        // SAFETY: `ip` is valid for `ip_length` bytes and `bytes.len() + 1 <= ip_length`.
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), ip.cast::<u8>(), bytes.len());
            *ip.add(bytes.len()) = 0;
        }
        Some(0)
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
