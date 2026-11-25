mod common;

use std::ptr;
use std::slice;
use std::thread;
use std::time::Duration;

use common::{address, client, empty_event, pump_until, server};
use enet::{
    enet_host_check_events, enet_host_connect, enet_host_destroy, enet_host_flush,
    enet_host_service, enet_packet_create, enet_packet_create_offset, enet_packet_dispose,
    enet_packet_get_data, enet_packet_get_length, enet_peer_disconnect, enet_peer_disconnect_later,
    enet_peer_disconnect_now, enet_peer_get_state, enet_peer_reset, enet_peer_send,
    enet_peer_timeout, ENetEvent, GlueHost, PacketHandle, PeerRecord,
};

const CONNECT: i32 = 1;
const DISCONNECT: i32 = 2;
const RECEIVE: i32 = 3;
const DISCONNECTED: i32 = 0;
const RELIABLE: u32 = 1;
const UNSEQUENCED: u32 = 2;
const INSTANT: u32 = 16;
const UNTHROTTLED: u32 = 32;
const CHANNELS: usize = 3;
const DEADLINE_MS: u64 = 3000;

/// A connected server and client and the peer handles each side sees.
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

    fn destroy(self) {
        // SAFETY: both hosts are live and destroyed once.
        unsafe {
            enet_host_destroy(self.server);
            enet_host_destroy(self.client);
        }
    }
}

/// One event as a test sees it, with any packet copied out and disposed.
#[derive(Debug, Clone, PartialEq)]
struct Seen {
    kind: i32,
    peer: *mut PeerRecord,
    channel: u8,
    data: u32,
    bytes: Option<Vec<u8>>,
}

/// Connects a client to a server over `CHANNELS` channels.
fn connected() -> Pair {
    let (server, port) = server("127.0.0.1");
    let client = client();
    let target = address("127.0.0.1", port);
    // SAFETY: `client` is live and `target` is valid for the call.
    let client_peer = unsafe { enet_host_connect(client, &target, CHANNELS, 0) };
    assert!(!client_peer.is_null());
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

/// Copies a packet's bytes out of its handle.
fn bytes_of(packet: *const PacketHandle) -> Vec<u8> {
    // SAFETY: `packet` is a live handle.
    let length = unsafe { enet_packet_get_length(packet) } as usize;
    if length == 0 {
        return Vec::new();
    }
    // SAFETY: a live handle's data is valid for `length` bytes.
    unsafe { slice::from_raw_parts(enet_packet_get_data(packet), length) }.to_vec()
}

/// Converts an event, copying out and disposing its packet.
fn seen(event: &ENetEvent) -> Seen {
    let bytes = (!event.packet.is_null()).then(|| {
        let bytes = bytes_of(event.packet);
        // SAFETY: a received packet handle is owned by the caller and disposed once.
        unsafe { enet_packet_dispose(event.packet) };
        bytes
    });
    Seen {
        kind: event.kind,
        peer: event.peer,
        channel: event.channel_id,
        data: event.data,
        bytes,
    }
}

/// Services `hosts` and records `target`'s events until `done` accepts the list so far.
///
/// Packets received by other hosts are disposed.
fn collect(
    hosts: &[*mut GlueHost],
    target: *mut GlueHost,
    deadline_ms: u64,
    done: impl Fn(&[Seen]) -> bool,
) -> Vec<Seen> {
    let mut events = Vec::new();
    pump_until(hosts, deadline_ms, |host, event| {
        let event = seen(event);
        if host != target {
            return false;
        }
        events.push(event);
        done(&events)
    });
    events
}

/// Records `target`'s events until the first receive and returns it.
fn first_receive(hosts: &[*mut GlueHost], target: *mut GlueHost) -> Seen {
    collect(hosts, target, DEADLINE_MS, |events| {
        events.iter().any(|event| event.kind == RECEIVE)
    })
    .into_iter()
    .find(|event| event.kind == RECEIVE)
    .expect("no packet arrived")
}

fn create(bytes: &[u8], flags: u32) -> *mut PacketHandle {
    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create(bytes.as_ptr(), bytes.len(), flags) };
    assert!(!packet.is_null());
    packet
}

fn send(peer: *mut PeerRecord, channel: u8, packet: *mut PacketHandle) -> i32 {
    // SAFETY: `peer` is a live record and `packet` a live handle the caller owns.
    unsafe { enet_peer_send(peer, channel, packet) }
}

fn dispose(packet: *mut PacketHandle) {
    // SAFETY: `packet` is a live handle the caller owns, disposed once.
    unsafe { enet_packet_dispose(packet) };
}

fn payload(length: usize, first: u8) -> Vec<u8> {
    let mut bytes: Vec<u8> = (0..length).map(|index| (index % 251) as u8).collect();
    if let Some(byte) = bytes.first_mut() {
        *byte = first;
    }
    bytes
}

fn state(peer: *const PeerRecord) -> i32 {
    // SAFETY: `peer` is a live record.
    unsafe { enet_peer_get_state(peer) }
}

/// Sends `bytes` from the client and returns what the server receives.
fn client_to_server(flags: u32, channel: u8, bytes: &[u8]) -> Seen {
    let pair = connected();
    assert_eq!(send(pair.client_peer, channel, create(bytes, flags)), 0);
    let received = first_receive(&pair.hosts(), pair.server);
    assert_eq!(received.peer, pair.server_peer);
    pair.destroy();
    received
}

#[test]
fn reliable_packet_arrives_on_channel_0() {
    let bytes = payload(100, 0xA1);

    let received = client_to_server(RELIABLE, 0, &bytes);

    assert_eq!(received.channel, 0);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
}

#[test]
fn unthrottled_sequenced_arrives_on_channel_1() {
    let bytes = payload(100, 0xB2);

    let received = client_to_server(UNTHROTTLED, 1, &bytes);

    assert_eq!(received.channel, 1);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
}

#[test]
fn unsequenced_arrives_on_channel_2() {
    let bytes = payload(100, 0xC3);

    let received = client_to_server(UNSEQUENCED, 2, &bytes);

    assert_eq!(received.channel, 2);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
}

#[test]
fn unthrottled_burst_arrives_in_order() {
    let pair = connected();
    for first in 0..8 {
        assert_eq!(
            send(
                pair.client_peer,
                1,
                create(&payload(64, first), UNTHROTTLED)
            ),
            0
        );
    }

    let events = collect(&pair.hosts(), pair.server, DEADLINE_MS, |events| {
        events.iter().filter(|event| event.kind == RECEIVE).count() == 8
    });

    let firsts: Vec<u8> = events
        .iter()
        .filter(|event| event.kind == RECEIVE)
        .map(|event| event.bytes.as_ref().unwrap()[0])
        .collect();
    assert_eq!(firsts, (0..8).collect::<Vec<u8>>());
    pair.destroy();
}

#[test]
fn zero_length_packet_round_trips() {
    let pair = connected();
    // SAFETY: a null pointer with length 0 is allowed.
    let packet = unsafe { enet_packet_create(ptr::null(), 0, RELIABLE) };
    assert!(!packet.is_null());
    // SAFETY: `packet` is a live handle.
    assert_eq!(unsafe { enet_packet_get_length(packet) }, 0);

    assert_eq!(send(pair.client_peer, 0, packet), 0);
    let received = first_receive(&pair.hosts(), pair.server);

    assert_eq!(received.bytes, Some(Vec::new()));
    pair.destroy();
}

#[test]
fn create_offset_copies_the_tail() {
    let bytes = [1u8, 2, 3, 4, 5];

    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create_offset(bytes.as_ptr(), 5, 2, RELIABLE) };

    assert!(!packet.is_null());
    assert_eq!(bytes_of(packet), [3, 4, 5]);
    dispose(packet);
}

#[test]
fn create_offset_rejects_offset_past_length() {
    let bytes = [1u8, 2, 3, 4, 5];

    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create_offset(bytes.as_ptr(), 5, 6, RELIABLE) };

    assert!(packet.is_null());
}

#[test]
fn create_copies_the_callers_bytes() {
    let mut bytes = payload(16, 0x11);
    let packet = create(&bytes, RELIABLE);

    bytes[0] = 0x22;

    assert_eq!(bytes_of(packet)[0], 0x11);
    dispose(packet);
}

#[test]
fn send_consumes_the_handle() {
    let pair = connected();
    let bytes = payload(200, 0xD4);
    let packet = create(&bytes, RELIABLE);

    assert_eq!(send(pair.server_peer, 0, packet), 0);

    let received = first_receive(&pair.hosts(), pair.client);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
    pair.destroy();
}

#[test]
fn instant_send_goes_out_without_service() {
    let pair = connected();
    let bytes = payload(50, 0xE5);

    assert_eq!(
        send(pair.client_peer, 0, create(&bytes, RELIABLE | INSTANT)),
        0
    );

    let received = first_receive(&[pair.server], pair.server);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
    pair.destroy();
}

#[test]
fn failed_send_leaves_the_handle() {
    let pair = connected();
    let packet = create(&payload(10, 0x01), RELIABLE);

    assert_eq!(send(pair.server_peer, 9, packet), -1);

    // SAFETY: a failed send leaves the handle live and with the caller.
    assert_eq!(unsafe { enet_packet_get_length(packet) }, 10);
    dispose(packet);
    pair.destroy();
}

#[test]
fn send_on_stale_peer_handle_fails() {
    let pair = connected();
    // SAFETY: `client_peer` is a live record.
    unsafe { enet_peer_disconnect(pair.client_peer, 0) };
    let events = collect(&pair.hosts(), pair.server, DEADLINE_MS, |events| {
        events.iter().any(|event| event.kind == DISCONNECT)
    });
    assert!(events.iter().any(|event| event.kind == DISCONNECT));
    assert_eq!(state(pair.server_peer), DISCONNECTED);
    let packet = create(&payload(10, 0x02), RELIABLE);

    assert_eq!(send(pair.server_peer, 0, packet), -1);

    assert_eq!(bytes_of(packet).len(), 10);
    dispose(packet);
    pair.destroy();
}

#[test]
fn disconnect_reason_reaches_the_other_side() {
    let pair = connected();

    // SAFETY: `server_peer` is a live record.
    unsafe { enet_peer_disconnect(pair.server_peer, 16) };

    let mut client_data = None;
    let mut server_peer = None;
    let done = pump_until(&pair.hosts(), DEADLINE_MS, |host, event| {
        let event = seen(event);
        if event.kind == DISCONNECT {
            if host == pair.client {
                client_data = Some(event.data);
            } else {
                server_peer = Some(event.peer);
            }
        }
        client_data.is_some() && server_peer.is_some()
    });
    assert!(done, "disconnect did not complete on both sides");
    assert_eq!(client_data, Some(16));
    assert_eq!(server_peer, Some(pair.server_peer));
    pair.destroy();
}

#[test]
fn disconnect_now_sends_reason_and_no_local_event() {
    let pair = connected();

    // SAFETY: `server_peer` is a live record.
    unsafe { enet_peer_disconnect_now(pair.server_peer, 1) };
    let mut event = empty_event();
    // SAFETY: `server` is live and `event` is valid for writes.
    assert_eq!(
        unsafe { enet_host_check_events(pair.server, &mut event) },
        0
    );

    let mut server_events = Vec::new();
    let mut client_data = None;
    pump_until(&pair.hosts(), DEADLINE_MS, |host, event| {
        let event = seen(event);
        if host == pair.server {
            server_events.push(event);
        } else if event.kind == DISCONNECT {
            client_data = Some(event.data);
        }
        client_data.is_some()
    });
    assert_eq!(client_data, Some(1));
    assert!(server_events.is_empty(), "server saw {server_events:?}");
    assert_eq!(state(pair.server_peer), DISCONNECTED);
    pair.destroy();
}

#[test]
fn disconnect_later_flushes_queued_packets_first() {
    let pair = connected();
    let bytes = payload(300, 0xF6);
    assert_eq!(send(pair.server_peer, 0, create(&bytes, RELIABLE)), 0);

    // SAFETY: `server_peer` is a live record.
    unsafe { enet_peer_disconnect_later(pair.server_peer, 3) };

    let events = collect(&pair.hosts(), pair.client, DEADLINE_MS, |events| {
        events.iter().any(|event| event.kind == DISCONNECT)
    });
    let kinds: Vec<(i32, u32)> = events
        .iter()
        .map(|event| (event.kind, event.data))
        .collect();
    assert_eq!(kinds, [(RECEIVE, 0), (DISCONNECT, 3)]);
    assert_eq!(events[0].bytes.as_deref(), Some(&bytes[..]));
    pair.destroy();
}

#[test]
fn reset_drops_the_peer_silently() {
    let pair = connected();

    // SAFETY: `server_peer` is a live record.
    unsafe { enet_peer_reset(pair.server_peer) };

    assert_eq!(state(pair.server_peer), DISCONNECTED);
    let mut events = Vec::new();
    pump_until(&pair.hosts(), 300, |_, event| {
        events.push(seen(event));
        false
    });
    assert!(events.is_empty(), "saw {events:?}");
    pair.destroy();
}

#[test]
fn timeout_arrives_as_disconnect() {
    let pair = connected();
    // SAFETY: `server_peer` is a live record.
    unsafe { enet_peer_timeout(pair.server_peer, 0, 500, 500) };

    // SAFETY: the client host is live and destroyed once; destroying it notifies no peer.
    unsafe { enet_host_destroy(pair.client) };

    let events = collect(&[pair.server], pair.server, DEADLINE_MS, |events| {
        events.iter().any(|event| event.kind == DISCONNECT)
    });
    let disconnect = events
        .iter()
        .find(|event| event.kind == DISCONNECT)
        .expect("no timeout within 3 s");
    assert_eq!(disconnect.peer, pair.server_peer);
    assert_eq!(disconnect.data, 0);
    // SAFETY: the server host is live and destroyed once.
    unsafe { enet_host_destroy(pair.server) };
}

#[test]
fn packet_outlives_its_host() {
    let pair = connected();
    assert_eq!(
        send(pair.client_peer, 0, create(&payload(500, 0x7E), RELIABLE)),
        0
    );
    let mut packet = ptr::null_mut();
    pump_until(&pair.hosts(), DEADLINE_MS, |host, event| {
        if host == pair.server && event.kind == RECEIVE {
            packet = event.packet;
            return true;
        }
        false
    });
    assert!(!packet.is_null(), "no packet arrived");

    pair.destroy();

    // SAFETY: a received handle owns its data independently of its host.
    assert_eq!(unsafe { enet_packet_get_length(packet) }, 500);
    // SAFETY: as above; the data is valid for 500 bytes.
    assert_eq!(unsafe { *enet_packet_get_data(packet) }, 0x7E);
    dispose(packet);
}

#[test]
fn check_events_drains_what_one_service_pass_queued() {
    let pair = connected();
    let first = payload(30, 0x11);
    let second = payload(30, 0x22);
    assert_eq!(send(pair.client_peer, 0, create(&first, RELIABLE)), 0);
    assert_eq!(send(pair.client_peer, 0, create(&second, RELIABLE)), 0);
    // SAFETY: `client` is live.
    unsafe { enet_host_flush(pair.client) };
    // Both packets are in the server's socket before its one service pass reads them.
    thread::sleep(Duration::from_millis(100));

    let mut event = empty_event();
    // SAFETY: `server` is live and `event` is valid for writes.
    assert_eq!(
        unsafe { enet_host_service(pair.server, &mut event, 100) },
        1
    );
    let from_service = seen(&event);
    // SAFETY: as above.
    let queued = unsafe { enet_host_check_events(pair.server, &mut event) };
    let from_check = seen(&event);
    // SAFETY: as above.
    let drained = unsafe { enet_host_check_events(pair.server, &mut event) };

    assert_eq!(from_service.kind, RECEIVE);
    assert_eq!(from_service.bytes.as_deref(), Some(&first[..]));
    assert_eq!(queued, 1);
    assert_eq!(from_check.kind, RECEIVE);
    assert_eq!(from_check.peer, pair.server_peer);
    assert_eq!(from_check.bytes.as_deref(), Some(&second[..]));
    assert_eq!(drained, 0);
    assert_eq!(event.kind, 0);
    pair.destroy();
}

#[test]
fn service_with_null_event_keeps_the_packet_queued() {
    let pair = connected();
    let bytes = payload(40, 0x5A);
    assert_eq!(
        send(pair.client_peer, 0, create(&bytes, RELIABLE | INSTANT)),
        0
    );

    // SAFETY: `server` is live; a null event is rejected before servicing.
    assert_eq!(
        unsafe { enet_host_service(pair.server, ptr::null_mut(), 50) },
        -1
    );
    // SAFETY: as above.
    assert_eq!(
        unsafe { enet_host_check_events(pair.server, ptr::null_mut()) },
        -1
    );

    let received = first_receive(&pair.hosts(), pair.server);
    assert_eq!(received.bytes.as_deref(), Some(&bytes[..]));
    pair.destroy();
}

#[test]
fn null_handles_return_errors() {
    let pair = connected();
    let packet = create(&payload(8, 0x33), RELIABLE);

    // SAFETY: every call takes null handles or live ones.
    unsafe {
        assert_eq!(enet_peer_send(ptr::null_mut(), 0, ptr::null_mut()), -1);
        assert_eq!(enet_peer_send(ptr::null_mut(), 0, packet), -1);
        assert_eq!(enet_peer_send(pair.server_peer, 0, ptr::null_mut()), -1);
        assert_eq!(enet_packet_get_length(ptr::null()), 0);
        assert!(enet_packet_get_data(ptr::null()).is_null());
        assert!(enet_packet_create(ptr::null(), 4, RELIABLE).is_null());
        assert!(enet_packet_create_offset(ptr::null(), 4, 1, RELIABLE).is_null());
        enet_packet_dispose(ptr::null_mut());
        enet_peer_disconnect(ptr::null_mut(), 0);
        enet_peer_disconnect_now(ptr::null_mut(), 0);
        enet_peer_disconnect_later(ptr::null_mut(), 0);
        enet_peer_reset(ptr::null_mut());
        assert_eq!(enet_packet_get_length(packet), 8);
    }
    dispose(packet);
    pair.destroy();
}
