//! The receive limit a listening host applies, and its absence on a client host.

mod common;

use std::slice;

use common::{client, connect, pump_until, server};
use enet::{
    enet_host_destroy, enet_packet_create, enet_packet_dispose, enet_packet_get_data,
    enet_packet_get_length, enet_peer_get_state, enet_peer_send, ENetEvent, GlueHost, PacketHandle,
    PeerRecord,
};

const CONNECT: i32 = 1;
const DISCONNECT: i32 = 2;
const RECEIVE: i32 = 3;
const DISCONNECTED: i32 = 0;
const CONNECTED: i32 = 5;
const RELIABLE: u32 = 1;
/// `DisconnectReason.PACKET_CORRUPTED`.
const PACKET_CORRUPTED: u32 = 16;
const DEADLINE_MS: u64 = 5000;

/// A connected listening host and client and the peer handles each side sees.
struct Pair {
    server: *mut GlueHost,
    client: *mut GlueHost,
    server_peer: *mut PeerRecord,
    client_peer: *mut PeerRecord,
}

impl Pair {
    fn hosts(&self) -> [*mut GlueHost; 2] {
        [self.server, self.client]
    }
}

/// One event as a test sees it, with any packet copied out and disposed.
#[derive(Debug, Clone, PartialEq)]
struct Seen {
    host: *mut GlueHost,
    kind: i32,
    peer: *mut PeerRecord,
    data: u32,
    bytes: Option<Vec<u8>>,
}

/// Connects a fresh client to the listening host `server` on `port`.
fn connect_client(server: *mut GlueHost, port: u16) -> Pair {
    let client = client();
    let client_peer = connect(client, port);
    let mut server_peer = None;
    let mut client_up = false;
    let done = pump_until(&[server, client], DEADLINE_MS, |host, event| {
        if event.kind == CONNECT {
            if host == server {
                server_peer = Some(event.peer);
            } else {
                client_up = true;
            }
        }
        server_peer.is_some() && client_up
    });
    assert!(done, "connection did not complete");
    Pair {
        server,
        client,
        server_peer: server_peer.unwrap(),
        client_peer,
    }
}

fn connected() -> Pair {
    let (server, port) = server("127.0.0.1");
    connect_client(server, port)
}

fn destroy(hosts: &[*mut GlueHost]) {
    for &host in hosts {
        // SAFETY: each host is live and destroyed once.
        unsafe { enet_host_destroy(host) };
    }
}

fn payload(length: usize) -> Vec<u8> {
    (0..length).map(|index| (index % 251) as u8).collect()
}

/// Queues a reliable copy of `bytes` to `peer`.
fn send_reliable(peer: *mut PeerRecord, bytes: &[u8]) {
    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create(bytes.as_ptr(), bytes.len(), RELIABLE) };
    assert!(!packet.is_null());
    // SAFETY: `peer` is a live record and `packet` a live handle the caller owns.
    assert_eq!(unsafe { enet_peer_send(peer, 0, packet) }, 0);
}

fn bytes_of(packet: *const PacketHandle) -> Vec<u8> {
    // SAFETY: `packet` is a live handle.
    let length = unsafe { enet_packet_get_length(packet) } as usize;
    // SAFETY: a live handle's data is valid for `length` bytes.
    unsafe { slice::from_raw_parts(enet_packet_get_data(packet), length) }.to_vec()
}

/// Converts an event, copying out and disposing its packet.
fn seen(host: *mut GlueHost, event: &ENetEvent) -> Seen {
    let bytes = (!event.packet.is_null()).then(|| {
        let bytes = bytes_of(event.packet);
        // SAFETY: a received packet handle is owned by the caller and disposed once.
        unsafe { enet_packet_dispose(event.packet) };
        bytes
    });
    Seen {
        host,
        kind: event.kind,
        peer: event.peer,
        data: event.data,
        bytes,
    }
}

/// Services `hosts` and records every event until `done` accepts the list so far.
fn collect(hosts: &[*mut GlueHost], done: impl Fn(&[Seen]) -> bool) -> Vec<Seen> {
    let mut events = Vec::new();
    pump_until(hosts, DEADLINE_MS, |host, event| {
        events.push(seen(host, event));
        done(&events)
    });
    events
}

fn state(peer: *const PeerRecord) -> i32 {
    // SAFETY: `peer` is a live record.
    unsafe { enet_peer_get_state(peer) }
}

#[test]
fn packet_of_8192_bytes_reaches_a_listening_host() {
    let pair = connected();
    let bytes = payload(8192);

    send_reliable(pair.client_peer, &bytes);
    let events = collect(&pair.hosts(), |events| {
        events.iter().any(|event| event.host == pair.server)
    });

    let received = events
        .iter()
        .find(|event| event.host == pair.server)
        .expect("the listening host saw no event");
    assert_eq!(received.kind, RECEIVE);
    assert_eq!(received.peer, pair.server_peer);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
    destroy(&pair.hosts());
}

#[test]
fn packet_of_8193_bytes_disconnects_the_sender_with_reason_16() {
    let pair = connected();

    send_reliable(pair.client_peer, &payload(8193));
    let events = collect(&pair.hosts(), |events| {
        let server_done = events
            .iter()
            .any(|event| event.host == pair.server && event.kind == DISCONNECT);
        let client_done = events
            .iter()
            .any(|event| event.host == pair.client && event.kind == DISCONNECT);
        server_done && client_done
    });

    let server_events: Vec<_> = events
        .iter()
        .filter(|event| event.host == pair.server)
        .collect();
    assert!(
        server_events.iter().all(|event| event.kind != RECEIVE),
        "the listening host delivered the oversized packet"
    );
    let server_disconnect = server_events
        .iter()
        .find(|event| event.kind == DISCONNECT)
        .expect("the listening host reported no disconnect");
    assert_eq!(server_disconnect.peer, pair.server_peer);
    assert_eq!(server_disconnect.data, PACKET_CORRUPTED);
    assert!(server_disconnect.bytes.is_none());
    let client_disconnect = events
        .iter()
        .find(|event| event.host == pair.client && event.kind == DISCONNECT)
        .expect("the sender saw no disconnect");
    assert_eq!(client_disconnect.data, PACKET_CORRUPTED);
    assert_eq!(state(pair.server_peer), DISCONNECTED);

    // SAFETY: the old client is live and destroyed once.
    unsafe { enet_host_destroy(pair.client) };
    // SAFETY: `server` is live; its socket is bound.
    let port = unsafe { &*pair.server }.local_addr().unwrap().port();
    let next = connect_client(pair.server, port);
    assert_eq!(state(next.server_peer), CONNECTED);
    destroy(&next.hosts());
}

#[test]
fn client_host_has_no_receive_limit() {
    let pair = connected();
    let bytes = payload(20000);

    send_reliable(pair.server_peer, &bytes);
    let events = collect(&pair.hosts(), |events| {
        events.iter().any(|event| event.host == pair.client)
    });

    let received = events
        .iter()
        .find(|event| event.host == pair.client)
        .expect("the client host saw no event");
    assert_eq!(received.kind, RECEIVE);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
    destroy(&pair.hosts());
}
