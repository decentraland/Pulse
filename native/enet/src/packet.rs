use std::os::raw::c_int;
use std::ptr;
use std::slice;

use rusty_enet::{Packet, PacketKind};

use crate::guard::guard;

/// `PacketFlags.Reliable`.
const RELIABLE: u32 = 1 << 0;
/// `PacketFlags.Unsequenced`.
const UNSEQUENCED: u32 = 1 << 1;
/// `PacketFlags.UnreliableFragmented`.
const UNRELIABLE_FRAGMENTED: u32 = 1 << 3;
/// `PacketFlags.Instant`.
const INSTANT: u32 = 1 << 4;
/// `PacketFlags.Unthrottled`.
const UNTHROTTLED: u32 = 1 << 5;

/// A packet handle: a `rusty_enet` packet and whether a send flushes at once.
///
/// Handed out as `Box::into_raw`. The packet owns its bytes and is reference counted by
/// `rusty_enet`, so a handle stays valid after its host is destroyed.
pub struct PacketHandle {
    pub(crate) packet: Packet,
    pub(crate) instant: bool,
}

impl PacketHandle {
    /// Wraps a packet received from a peer.
    pub(crate) fn received(packet: Packet) -> PacketHandle {
        PacketHandle {
            packet,
            instant: false,
        }
    }

    /// Builds a packet from `bytes` and ENet-CSharp `PacketFlags`.
    ///
    /// `RELIABLE` wins over the unreliable flags, `NO_ALLOCATE` and unknown bits are ignored.
    fn new(bytes: Vec<u8>, flags: u32) -> PacketHandle {
        let sequenced = flags & UNSEQUENCED == 0;
        let kind = if flags & RELIABLE != 0 {
            PacketKind::Reliable
        } else if flags & UNRELIABLE_FRAGMENTED != 0 {
            PacketKind::AlwaysUnreliable { sequenced }
        } else {
            PacketKind::Unreliable { sequenced }
        };
        let packet = if flags & UNTHROTTLED != 0 {
            Packet::new_unthrottled(bytes, kind)
        } else {
            Packet::new(bytes, kind)
        };
        PacketHandle {
            packet,
            instant: flags & INSTANT != 0,
        }
    }
}

/// Copies `length` bytes starting `offset` bytes into `data`. Returns `None` when `data` is null
/// and `length` is not 0.
///
/// # Safety
///
/// When `length` is not 0, `data` must be null or valid for `offset + length` bytes of reads.
unsafe fn copy(data: *const u8, offset: usize, length: usize) -> Option<Vec<u8>> {
    if length == 0 {
        return Some(Vec::new());
    }
    if data.is_null() {
        return None;
    }
    // SAFETY: the caller guarantees `data` is valid for `offset + length` bytes.
    Some(unsafe { slice::from_raw_parts(data.add(offset), length) }.to_vec())
}

/// Creates a packet holding a copy of `data_length` bytes. Returns null when `data` is null and
/// `data_length` is not 0; a null `data` with length 0 creates an empty packet.
///
/// # Safety
///
/// `data` must be null or valid for `data_length` bytes of reads.
#[no_mangle]
pub unsafe extern "C" fn enet_packet_create(
    data: *const u8,
    data_length: usize,
    flags: u32,
) -> *mut PacketHandle {
    guard(ptr::null_mut(), || {
        // SAFETY: the caller guarantees `data` is null or valid for `data_length` bytes.
        let bytes = unsafe { copy(data, 0, data_length) }?;
        Some(Box::into_raw(Box::new(PacketHandle::new(bytes, flags))))
    })
}

/// Creates a packet holding a copy of the `data_length - data_offset` bytes that start at
/// `data + data_offset`. Returns null when the offset exceeds the length, or when `data` is null
/// and the copied length is not 0.
///
/// # Safety
///
/// `data` must be null or valid for `data_length` bytes of reads.
#[no_mangle]
pub unsafe extern "C" fn enet_packet_create_offset(
    data: *const u8,
    data_length: usize,
    data_offset: usize,
    flags: u32,
) -> *mut PacketHandle {
    guard(ptr::null_mut(), || {
        let length = data_length.checked_sub(data_offset)?;
        // SAFETY: the caller guarantees `data` is null or valid for `data_offset + length`
        // bytes.
        let bytes = unsafe { copy(data, data_offset, length) }?;
        Some(Box::into_raw(Box::new(PacketHandle::new(bytes, flags))))
    })
}

/// Returns the packet's bytes, or null for a null handle. Callers read
/// `enet_packet_get_length` bytes; for an empty packet the pointer is not to be dereferenced.
///
/// # Safety
///
/// `packet` must be null or a live handle.
#[no_mangle]
pub unsafe extern "C" fn enet_packet_get_data(packet: *const PacketHandle) -> *const u8 {
    guard(ptr::null(), || {
        // SAFETY: the caller guarantees `packet` is null or a live handle.
        Some(unsafe { packet.as_ref() }?.packet.data().as_ptr())
    })
}

/// Returns the packet's length in bytes, or 0 for a null handle.
///
/// # Safety
///
/// `packet` must be null or a live handle.
#[no_mangle]
pub unsafe extern "C" fn enet_packet_get_length(packet: *const PacketHandle) -> c_int {
    guard(0, || {
        // SAFETY: the caller guarantees `packet` is null or a live handle.
        c_int::try_from(unsafe { packet.as_ref() }?.packet.data().len()).ok()
    })
}

/// Frees a packet handle the caller owns. Null is a no-op.
///
/// A handle passed to a successful `enet_peer_send` is no longer the caller's: disposing it is a
/// double free.
///
/// # Safety
///
/// `packet` must be null or a live handle owned by the caller; it is invalid afterwards.
#[no_mangle]
pub unsafe extern "C" fn enet_packet_dispose(packet: *mut PacketHandle) {
    guard((), || {
        if !packet.is_null() {
            // SAFETY: the caller guarantees `packet` came from `Box::into_raw` and is owned.
            drop(unsafe { Box::from_raw(packet) });
        }
        Some(())
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn handle(flags: u32) -> PacketHandle {
        PacketHandle::new(vec![1, 2, 3], flags)
    }

    #[test]
    fn no_flags_is_sequenced_unreliable() {
        let handle = handle(0);
        assert_eq!(
            handle.packet.kind(),
            PacketKind::Unreliable { sequenced: true }
        );
        assert!(!handle.packet.is_unthrottled());
        assert!(!handle.instant);
    }

    #[test]
    fn reliable_maps_to_reliable() {
        assert_eq!(handle(RELIABLE).packet.kind(), PacketKind::Reliable);
    }

    #[test]
    fn unsequenced_maps_to_unsequenced_unreliable() {
        assert_eq!(
            handle(UNSEQUENCED).packet.kind(),
            PacketKind::Unreliable { sequenced: false }
        );
    }

    #[test]
    fn unreliable_fragmented_maps_to_always_unreliable() {
        assert_eq!(
            handle(UNRELIABLE_FRAGMENTED).packet.kind(),
            PacketKind::AlwaysUnreliable { sequenced: true }
        );
        assert_eq!(
            handle(UNRELIABLE_FRAGMENTED | UNSEQUENCED).packet.kind(),
            PacketKind::AlwaysUnreliable { sequenced: false }
        );
    }

    #[test]
    fn reliable_wins_over_unreliable_flags() {
        let flags = RELIABLE | UNSEQUENCED | UNRELIABLE_FRAGMENTED;
        assert_eq!(handle(flags).packet.kind(), PacketKind::Reliable);
    }

    #[test]
    fn unthrottled_keeps_the_kind() {
        let handle = handle(UNTHROTTLED);
        assert!(handle.packet.is_unthrottled());
        assert_eq!(
            handle.packet.kind(),
            PacketKind::Unreliable { sequenced: true }
        );
    }

    #[test]
    fn instant_is_remembered() {
        assert!(handle(INSTANT | RELIABLE).instant);
    }

    #[test]
    fn unknown_and_no_allocate_bits_are_ignored() {
        let handle = handle(RELIABLE | 1 << 2 | 1 << 8 | 1 << 31);
        assert_eq!(handle.packet.kind(), PacketKind::Reliable);
        assert!(!handle.packet.is_unthrottled());
        assert!(!handle.instant);
        assert_eq!(handle.packet.data(), [1, 2, 3]);
    }
}
