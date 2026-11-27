//! The built cdylib exports every function of ENet-CSharp's interface under its C name.

use std::env;
use std::path::PathBuf;
use std::process::Command;

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

/// Builds the glue library with the Cargo running the tests and returns the cdylib path from
/// Cargo's own `compiler-artifact` message, so the library always matches the current sources.
fn built_library() -> PathBuf {
    const FILENAMES: &str = r#""filenames":["#;
    let cargo = env::var_os("CARGO").unwrap_or_else(|| env!("CARGO").into());
    let output = Command::new(cargo)
        .args(["build", "--lib", "--message-format=json"])
        .current_dir(env!("CARGO_MANIFEST_DIR"))
        .output()
        .expect("cargo could not be started");
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(output.status.success(), "cargo build failed:\n{stderr}");

    String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter(|line| line.contains(r#""reason":"compiler-artifact""#))
        .filter(|line| line.contains(r#""cdylib""#))
        .filter_map(|line| {
            let start = line.find(FILENAMES)? + FILENAMES.len();
            let list = &line[start..start + line[start..].find(']')?];
            list.split(',')
                .map(|name| name.trim().trim_matches('"').replace("\\\\", "\\"))
                .find(|name| {
                    [".dll", ".so", ".dylib"]
                        .iter()
                        .any(|extension| name.ends_with(extension))
                })
        })
        .next()
        .map(PathBuf::from)
        .unwrap_or_else(|| panic!("cargo reported no cdylib path:\n{stderr}"))
}

/// Checks that each of the 31 names resolves in the built library; it checks presence, not that
/// nothing else is exported.
#[test]
fn built_library_exports_the_31_functions() {
    let path = built_library();
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
