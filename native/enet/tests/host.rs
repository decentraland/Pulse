mod common;

use std::ffi::{c_char, CStr};
use std::net::UdpSocket;
use std::ptr;
use std::time::{Duration, Instant};

use common::{
    address, client, connect, connect_to, empty_event, local_port, pump_for, pump_until, server,
    try_server,
};
use enet::{
    enet_host_check_events, enet_host_connect, enet_host_create, enet_host_destroy,
    enet_host_service, enet_peer_get_id, enet_peer_get_ip, enet_peer_get_mtu, enet_peer_get_port,
    enet_peer_get_rtt, enet_peer_get_state, enet_peer_timeout, GlueHost, PeerRecord,
};

const CONNECT: i32 = 1;
const DISCONNECT: i32 = 2;
const CONNECTED: i32 = 5;
const CONNECT_DEADLINE_MS: u64 = 3000;

/// Peers and data seen by both sides once a connection completes.
struct Connection {
    server_peer: *mut PeerRecord,
    server_data: u32,
    client_peer: *mut PeerRecord,
}

/// Pumps `server` and `client` until both report a connect event.
fn await_connect(server: *mut GlueHost, client: *mut GlueHost) -> Connection {
    let mut server_event = None;
    let mut client_peer = None;
    let done = pump_until(&[server, client], CONNECT_DEADLINE_MS, |host, event| {
        if event.kind == CONNECT {
            if host == server {
                server_event = Some((event.peer, event.data));
            } else {
                client_peer = Some(event.peer);
            }
        }
        server_event.is_some() && client_peer.is_some()
    });
    assert!(done, "connection did not complete");
    let (server_peer, server_data) = server_event.unwrap();
    Connection {
        server_peer,
        server_data,
        client_peer: client_peer.unwrap(),
    }
}

fn peer_ip(peer: *const PeerRecord) -> String {
    let mut buffer = [0 as c_char; 64];
    // SAFETY: `peer` is a live record and the buffer is valid for its length.
    let result = unsafe { enet_peer_get_ip(peer, buffer.as_mut_ptr(), buffer.len()) };
    assert_eq!(result, 0);
    // SAFETY: a successful call wrote a NUL-terminated string.
    unsafe { CStr::from_ptr(buffer.as_ptr()) }
        .to_str()
        .unwrap()
        .to_owned()
}

fn destroy(hosts: &[*mut GlueHost]) {
    for &host in hosts {
        // SAFETY: each host is live and destroyed once.
        unsafe { enet_host_destroy(host) };
    }
}

#[test]
fn client_and_server_see_connect() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    let requested = connect(client, port);

    let connection = await_connect(server, client);

    assert_eq!(connection.client_peer, requested);
    // SAFETY: the record lives until its host is destroyed.
    assert_eq!(unsafe { enet_peer_get_id(connection.server_peer) }, 0);
    destroy(&[server, client]);
}

#[test]
fn connect_data_reaches_server() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    connect_to(client, "127.0.0.1", port, 77);

    let connection = await_connect(server, client);

    assert_eq!(connection.server_data, 77);
    destroy(&[server, client]);
}

#[test]
fn peer_ip_is_dotted_v4_under_v4_bind() {
    let (server, port) = server("0.0.0.0");
    let client = client();
    connect(client, port);

    let connection = await_connect(server, client);

    assert_eq!(peer_ip(connection.server_peer), "127.0.0.1");
    destroy(&[server, client]);
}

#[test]
fn peer_ip_is_dotted_v4_under_v6_bind() {
    let (server, port) = server("::");
    let client = client();
    connect(client, port);

    let connection = await_connect(server, client);

    assert_eq!(peer_ip(connection.server_peer), "127.0.0.1");
    destroy(&[server, client]);
}

#[test]
fn peer_port_matches_client_socket() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    connect(client, port);

    let connection = await_connect(server, client);

    // SAFETY: the record lives until its host is destroyed.
    let peer_port = unsafe { enet_peer_get_port(connection.server_peer) };
    assert_eq!(peer_port, local_port(client));
    destroy(&[server, client]);
}

#[test]
fn glue_client_is_granted_mtu_1392() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    connect(client, port);

    let connection = await_connect(server, client);

    // SAFETY: the record lives until its host is destroyed.
    assert_eq!(unsafe { enet_peer_get_mtu(connection.server_peer) }, 1392);
    destroy(&[server, client]);
}

#[test]
fn peer_state_is_connected_after_connect() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    connect(client, port);

    let connection = await_connect(server, client);

    // SAFETY: the records live until their hosts are destroyed.
    unsafe {
        assert_eq!(enet_peer_get_state(connection.server_peer), CONNECTED);
        assert_eq!(enet_peer_get_state(connection.client_peer), CONNECTED);
    }
    destroy(&[server, client]);
}

#[test]
fn rtt_is_reported_in_milliseconds() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    connect(client, port);
    let connection = await_connect(server, client);

    pump_for(&[server, client], 200);

    // SAFETY: the records live until their hosts are destroyed.
    unsafe {
        assert!(enet_peer_get_rtt(connection.server_peer) <= 500);
        assert!(enet_peer_get_rtt(connection.client_peer) <= 500);
    }
    destroy(&[server, client]);
}

#[test]
fn service_times_out_with_zero() {
    let (server, _) = server("127.0.0.1");
    let mut event = empty_event();
    let started = Instant::now();

    // SAFETY: `server` is live and `event` is valid for writes.
    let result = unsafe { enet_host_service(server, &mut event, 30) };

    assert_eq!(result, 0);
    assert_eq!(event.kind, 0);
    assert!(started.elapsed() >= Duration::from_millis(20));
    destroy(&[server]);
}

#[test]
fn service_with_zero_timeout_does_not_wait() {
    let (server, _) = server("127.0.0.1");
    let mut event = empty_event();
    let started = Instant::now();

    // SAFETY: `server` is live and `event` is valid for writes.
    let result = unsafe { enet_host_service(server, &mut event, 0) };

    assert_eq!(result, 0);
    assert!(started.elapsed() < Duration::from_millis(10));
    destroy(&[server]);
}

#[test]
fn create_fails_when_port_is_taken() {
    let (server, port) = server("127.0.0.1");
    let taken = address("127.0.0.1", port);

    // SAFETY: `taken` is valid for the duration of the call.
    let second = unsafe { enet_host_create(&taken, 8, 0, 0, 0, 0) };

    assert!(second.is_null());
    destroy(&[server]);
}

#[test]
fn create_fails_above_peer_limit() {
    let bind = address("127.0.0.1", 0);

    // SAFETY: `bind` is valid for the duration of the call.
    let host = unsafe { enet_host_create(&bind, 4096, 0, 0, 0, 0) };

    assert!(host.is_null());
}

#[test]
fn junk_datagrams_are_ignored() {
    let (server, port) = server("127.0.0.1");
    let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
    sender.send_to(&[0xFF; 7], ("127.0.0.1", port)).unwrap();
    sender.send_to(&[0x01, 0x02], ("127.0.0.1", port)).unwrap();
    let mut event = empty_event();

    // SAFETY: `server` is live and `event` is valid for writes.
    let result = unsafe { enet_host_service(server, &mut event, 50) };

    assert_eq!(result, 0);
    let client = client();
    connect(client, port);
    await_connect(server, client);
    destroy(&[server, client]);
}

#[test]
fn peer_record_survives_until_destroy() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    connect(client, port);
    let connection = await_connect(server, client);
    // SAFETY: the record lives until its host is destroyed.
    let slot = unsafe { enet_peer_get_id(connection.server_peer) };
    // SAFETY: as above.
    unsafe { enet_peer_timeout(connection.server_peer, 0, 500, 500) };

    destroy(&[client]);
    let mut disconnected = None;
    let done = pump_until(&[server], 5000, |_, event| {
        if event.kind == DISCONNECT {
            disconnected = Some(event.peer);
        }
        disconnected.is_some()
    });

    assert!(done, "server did not time out the vanished client");
    assert_eq!(disconnected, Some(connection.server_peer));
    // SAFETY: the record lives until its host is destroyed.
    assert_eq!(unsafe { enet_peer_get_id(connection.server_peer) }, slot);
    destroy(&[server]);
}

#[test]
fn two_hosts_in_one_process_are_independent() {
    let (server_a, port_a) = server("127.0.0.1");
    let (server_b, port_b) = server("127.0.0.1");
    assert_ne!(port_a, port_b);
    let client_a = client();
    let client_b = client();
    connect(client_a, port_a);
    connect(client_b, port_b);

    let a = await_connect(server_a, client_a);
    let b = await_connect(server_b, client_b);
    destroy(&[server_a, client_a]);

    // SAFETY: the records of `server_b` and `client_b` live until those hosts are destroyed.
    unsafe {
        assert_eq!(enet_peer_get_id(b.server_peer), 0);
        assert_eq!(enet_peer_get_state(b.server_peer), CONNECTED);
    }
    assert_ne!(a.server_peer, b.server_peer);
    pump_for(&[server_b, client_b], 50);
    // SAFETY: as above.
    assert_eq!(unsafe { enet_peer_get_state(b.client_peer) }, CONNECTED);
    destroy(&[server_b, client_b]);
}

#[test]
fn null_address_client_reaches_v4_server() {
    let (server, port) = server("127.0.0.1");
    let client = client();
    connect_to(client, "127.0.0.1", port, 0);

    await_connect(server, client);

    destroy(&[server, client]);
}

#[test]
fn null_address_client_reaches_v6_server() {
    let Some((server, port)) = try_server("::1") else {
        println!("skipped: \"::1\" cannot be bound on this machine");
        return;
    };
    let client = client();
    connect_to(client, "::1", port, 0);

    let connection = await_connect(server, client);

    assert_eq!(peer_ip(connection.server_peer), "::1");
    destroy(&[server, client]);
}

#[test]
fn null_handles_return_errors() {
    let mut event = empty_event();
    let target = address("127.0.0.1", 1);
    let mut buffer = [0 as c_char; 64];
    // SAFETY: null handles are the case under test; the other pointers are valid.
    unsafe {
        assert_eq!(enet_host_service(ptr::null_mut(), &mut event, 0), -1);
        assert_eq!(enet_host_check_events(ptr::null_mut(), &mut event), -1);
        assert!(enet_host_connect(ptr::null_mut(), &target, 1, 0).is_null());
        assert_eq!(enet_peer_get_id(ptr::null()), 0);
        assert_eq!(
            enet_peer_get_ip(ptr::null(), buffer.as_mut_ptr(), buffer.len()),
            -1
        );
        enet_host_destroy(ptr::null_mut());
    }
}
