use std::os::raw::c_int;
use std::sync::OnceLock;
use std::time::Instant;

use crate::guard::guard;

/// ENet-CSharp's linked version, encoded as `(major << 16) | (minor << 8) | patch`.
const LINKED_VERSION: u32 = 0x0002_0409;

static EPOCH: OnceLock<Instant> = OnceLock::new();

/// Initializes the library; always succeeds and returns 0.
#[no_mangle]
pub extern "C" fn enet_initialize() -> c_int {
    guard(-1, || Some(0))
}

/// Deinitializes the library; nothing is held globally, so there is nothing to release.
#[no_mangle]
pub extern "C" fn enet_deinitialize() {
    guard((), || Some(()));
}

/// Returns the ENet version this library is compatible with.
#[no_mangle]
pub extern "C" fn enet_linked_version() -> u32 {
    guard(0, || Some(LINKED_VERSION))
}

/// Returns milliseconds since the first call in this process, wrapping at `u32::MAX`.
#[no_mangle]
pub extern "C" fn enet_time_get() -> u32 {
    guard(0, || {
        let epoch = EPOCH.get_or_init(Instant::now);
        Some(epoch.elapsed().as_millis() as u32)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn linked_version_is_2_4_9() {
        assert_eq!(enet_linked_version(), (2 << 16) | (4 << 8) | 9);
    }

    #[test]
    fn initialize_succeeds() {
        assert_eq!(enet_initialize(), 0);
        enet_deinitialize();
    }

    #[test]
    fn time_does_not_go_backwards() {
        let a = enet_time_get();
        let b = enet_time_get();
        assert!(b >= a);
    }
}
