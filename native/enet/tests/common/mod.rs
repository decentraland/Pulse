//! Helpers shared by the integration tests: hosts on ephemeral ports and a service pump.

#![allow(dead_code)]

use std::ffi::CString;
use std::ptr;
use std::time::{Duration, Instant};

use enet::{
    enet_address_set_ip, enet_host_connect, enet_host_create, enet_host_service, ENetAddress,
    ENetEvent, GlueHost, PeerRecord,
};

/// Peer slots given to a test server.
pub const SERVER_PEERS: usize = 32;

/// Builds an address from an IP literal and a port.
pub fn address(ip: &str, port: u16) -> ENetAddress {
    let mut address = ENetAddress {
        ipv6: [0; 16],
        port,
    };
    let text = CString::new(ip).unwrap();
    // SAFETY: both pointers are valid for the duration of the call.
    assert_eq!(
        unsafe { enet_address_set_ip(&mut address, text.as_ptr()) },
        0
    );
    address
}

/// Creates a host bound to `bind` on an ephemeral port, or null when the bind fails.
pub fn try_server(bind: &str) -> Option<(*mut GlueHost, u16)> {
    let address = address(bind, 0);
    // SAFETY: `address` is valid for the duration of the call.
    let host = unsafe { enet_host_create(&address, SERVER_PEERS, 0, 0, 0, 0) };
    if host.is_null() {
        return None;
    }
    Some((host, local_port(host)))
}

/// Creates a host bound to `bind` on an ephemeral port and returns it with that port.
pub fn server(bind: &str) -> (*mut GlueHost, u16) {
    try_server(bind).unwrap_or_else(|| panic!("server bound to {bind} could not be created"))
}

/// Creates a client host with a null address and one peer slot.
pub fn client() -> *mut GlueHost {
    // SAFETY: a null address is allowed.
    let host = unsafe { enet_host_create(ptr::null(), 1, 0, 0, 0, 0) };
    assert!(!host.is_null(), "client host could not be created");
    host
}

/// Starts a connection from `client` to `127.0.0.1:port`.
pub fn connect(client: *mut GlueHost, port: u16) -> *mut PeerRecord {
    connect_to(client, "127.0.0.1", port, 0)
}

/// Starts a connection from `client` to `ip:port`, sending `data`.
pub fn connect_to(client: *mut GlueHost, ip: &str, port: u16, data: u32) -> *mut PeerRecord {
    let address = address(ip, port);
    // SAFETY: `client` is a live host and `address` is valid for the call.
    let peer = unsafe { enet_host_connect(client, &address, 0, data) };
    assert!(!peer.is_null(), "connect to {ip}:{port} failed");
    peer
}

/// Returns the port a host's socket is bound to.
pub fn local_port(host: *mut GlueHost) -> u16 {
    // SAFETY: `host` is a live host created by `enet_host_create`.
    unsafe { &*host }.local_addr().unwrap().port()
}

/// Returns an event with every field cleared.
pub fn empty_event() -> ENetEvent {
    ENetEvent {
        kind: 0,
        peer: ptr::null_mut(),
        channel_id: 0,
        data: 0,
        packet: ptr::null_mut(),
    }
}

/// Services every host in turn until `pred` accepts an event or `deadline_ms` elapses.
///
/// Returns true when `pred` accepted an event.
pub fn pump_until(
    hosts: &[*mut GlueHost],
    deadline_ms: u64,
    mut pred: impl FnMut(*mut GlueHost, &ENetEvent) -> bool,
) -> bool {
    let deadline = Instant::now() + Duration::from_millis(deadline_ms);
    while Instant::now() < deadline {
        for &host in hosts {
            let mut event = empty_event();
            // SAFETY: `host` is a live host and `event` is valid for writes.
            let result = unsafe { enet_host_service(host, &mut event, 1) };
            assert!(result >= 0, "service failed");
            if result == 1 && pred(host, &event) {
                return true;
            }
        }
    }
    false
}

/// Services every host for `millis`, ignoring events.
pub fn pump_for(hosts: &[*mut GlueHost], millis: u64) {
    pump_until(hosts, millis, |_, _| false);
}
