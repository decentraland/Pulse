//! The built cdylib exports every function of ENet-CSharp's interface under its C name.

use std::env;
use std::path::PathBuf;

#[cfg(target_os = "windows")]
const LIBRARY_FILE: &str = "enet.dll";
#[cfg(target_os = "macos")]
const LIBRARY_FILE: &str = "libenet.dylib";
#[cfg(not(any(target_os = "windows", target_os = "macos")))]
const LIBRARY_FILE: &str = "libenet.so";

const EXPORTS: [&str; 31] = [
    "enet_initialize",
    "enet_deinitialize",
    "enet_linked_version",
    "enet_time_get",
    "enet_address_set_ip",
    "enet_address_set_hostname",
    "enet_address_get_ip",
    "enet_host_create",
    "enet_host_destroy",
    "enet_host_service",
    "enet_host_check_events",
    "enet_host_flush",
    "enet_host_connect",
    "enet_peer_get_id",
    "enet_peer_get_ip",
    "enet_peer_get_port",
    "enet_peer_get_mtu",
    "enet_peer_get_state",
    "enet_peer_get_rtt",
    "enet_peer_timeout",
    "enet_peer_ping_interval",
    "enet_peer_send",
    "enet_peer_disconnect",
    "enet_peer_disconnect_now",
    "enet_peer_disconnect_later",
    "enet_peer_reset",
    "enet_packet_create",
    "enet_packet_create_offset",
    "enet_packet_get_data",
    "enet_packet_get_length",
    "enet_packet_dispose",
];

/// Returns the cdylib path: the test binary sits in `<profile>/deps/`, the library in `<profile>/`.
fn built_library() -> PathBuf {
    let test_binary = env::current_exe().expect("test binary path");
    let profile_dir = test_binary
        .parent()
        .and_then(|deps| deps.parent())
        .expect("profile directory above deps");
    profile_dir.join(LIBRARY_FILE)
}

#[test]
fn built_library_exports_exactly_the_31_functions() {
    let path = built_library();
    assert!(path.is_file(), "Cargo did not build {}", path.display());
    // SAFETY: loading the glue library runs no initializer beyond the Rust runtime's.
    let library = unsafe { libloading::Library::new(&path) }
        .unwrap_or_else(|error| panic!("{} did not load: {error}", path.display()));

    let missing: Vec<&str> = EXPORTS
        .iter()
        .copied()
        .filter(|name| {
            // SAFETY: the symbol is only looked up, never called.
            unsafe { library.get::<unsafe extern "C" fn()>(name.as_bytes()) }.is_err()
        })
        .collect();

    assert!(
        missing.is_empty(),
        "missing exports: {}",
        missing.join(", ")
    );
}
