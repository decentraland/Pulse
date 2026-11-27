//! MTU regression: a raw `rusty_enet` client at 1392 or 1280 bytes against a glue server.

mod common;

use std::net::{SocketAddr, UdpSocket};
use std::slice;
use std::time::{Duration, Instant};

use common::{empty_event, server};
use enet::{
    enet_host_destroy, enet_host_service, enet_packet_create, enet_packet_dispose,
    enet_packet_get_data, enet_packet_get_length, enet_peer_get_mtu, enet_peer_send, ENetEvent,
    GlueHost, PeerRecord,
};
use rusty_enet::{EventNoRef, Host, HostSettings, Packet, PacketKind, PeerID};

const CONNECT: i32 = 1;
const RECEIVE: i32 = 3;
const RELIABLE: u32 = 1;
const DEADLINE_MS: u64 = 3000;

/// What one service pass saw, on either side.
enum Seen<'a> {
    Server(&'a ENetEvent),
    Client(EventNoRef),
}

/// A glue server and a raw client connected to it.
struct Link {
    server: *mut GlueHost,
    server_peer: *mut PeerRecord,
    client: Host<UdpSocket>,
    client_peer: PeerID,
}

impl Drop for Link {
    fn drop(&mut self) {
        // SAFETY: the server host is live and destroyed once, here.
        unsafe { enet_host_destroy(self.server) };
    }
}

/// Services the glue server and the raw client in turn until `pred` accepts an event or the
/// deadline passes. Returns true when `pred` accepted one.
fn pump(
    server: *mut GlueHost,
    client: &mut Host<UdpSocket>,
    mut pred: impl FnMut(Seen<'_>) -> bool,
) -> bool {
    let deadline = Instant::now() + Duration::from_millis(DEADLINE_MS);
    while Instant::now() < deadline {
        let mut event = empty_event();
        // SAFETY: `server` is live and `event` is valid for writes.
        let result = unsafe { enet_host_service(server, &mut event, 1) };
        assert!(result >= 0, "server service failed");
        if result == 1 && pred(Seen::Server(&event)) {
            return true;
        }
        if let Some(event) = client.service().expect("client service failed") {
            if pred(Seen::Client(event.no_ref())) {
                return true;
            }
        }
    }
    false
}

/// Connects a raw client, with `mtu` set before connecting when given, to a glue server.
fn link(mtu: Option<u16>) -> Link {
    let (server, port) = server("127.0.0.1");
    let socket = UdpSocket::bind("127.0.0.1:0").unwrap();
    let settings = HostSettings {
        peer_limit: 1,
        channel_limit: 3,
        ..Default::default()
    };
    let Ok(mut client) = Host::new(socket, settings) else {
        panic!("raw client could not be created");
    };
    if let Some(mtu) = mtu {
        client.set_mtu(mtu).unwrap();
    }
    let target = SocketAddr::from(([127, 0, 0, 1], port));
    let client_peer = client.connect(target, 3, 0).unwrap().id();
    let mut server_peer = None;
    let mut client_up = false;
    let done = pump(server, &mut client, |seen| {
        match seen {
            Seen::Server(event) if event.kind == CONNECT => server_peer = Some(event.peer),
            Seen::Client(EventNoRef::Connect { .. }) => client_up = true,
            _ => {}
        }
        server_peer.is_some() && client_up
    });
    assert!(done, "connection did not complete");
    Link {
        server,
        server_peer: server_peer.unwrap(),
        client,
        client_peer,
    }
}

fn payload(length: usize) -> Vec<u8> {
    (0..length).map(|index| (index % 251) as u8).collect()
}

fn assert_payload(bytes: &[u8], length: usize) {
    assert_eq!(bytes.len(), length);
    for (index, &byte) in bytes.iter().enumerate() {
        assert_eq!(byte, (index % 251) as u8, "byte {index}");
    }
}

/// Sends `length` bytes of `kind` from the client and returns what the server receives.
fn client_sends(link: &mut Link, length: usize, channel: u8, kind: PacketKind) -> Vec<u8> {
    let packet = Packet::new(payload(length), kind);
    link.client
        .peer_mut(link.client_peer)
        .send(channel, &packet)
        .unwrap();
    let mut received = None;
    pump(link.server, &mut link.client, |seen| match seen {
        Seen::Server(event) if event.kind == RECEIVE => {
            // SAFETY: a received handle is live and owned here.
            let bytes = unsafe {
                let length = enet_packet_get_length(event.packet) as usize;
                let bytes = slice::from_raw_parts(enet_packet_get_data(event.packet), length);
                let bytes = bytes.to_vec();
                enet_packet_dispose(event.packet);
                bytes
            };
            received = Some(bytes);
            true
        }
        _ => false,
    });
    received.expect("no packet arrived")
}

#[test]
fn client_at_1392_delivers_1300_byte_reliable() {
    let mut link = link(None);

    let bytes = client_sends(&mut link, 1300, 0, PacketKind::Reliable);

    assert_payload(&bytes, 1300);
}

#[test]
fn client_at_1392_delivers_3000_byte_reliable() {
    let mut link = link(None);

    let bytes = client_sends(&mut link, 3000, 0, PacketKind::Reliable);

    assert_payload(&bytes, 3000);
}

#[test]
fn client_at_1392_delivers_1364_byte_unsequenced() {
    let mut link = link(None);

    let kind = PacketKind::Unreliable { sequenced: false };
    let bytes = client_sends(&mut link, 1364, 2, kind);

    assert_payload(&bytes, 1364);
}

#[test]
fn client_at_1280_is_granted_1280() {
    let link = link(Some(1280));

    // SAFETY: `server_peer` is a live record.
    assert_eq!(unsafe { enet_peer_get_mtu(link.server_peer) }, 1280);
    assert_eq!(link.client.peer(link.client_peer).mtu(), 1280);
}

#[test]
fn server_sends_3000_byte_reliable_to_1280_client() {
    let mut link = link(Some(1280));
    let bytes = payload(3000);
    // SAFETY: `bytes` is valid for its length.
    let packet = unsafe { enet_packet_create(bytes.as_ptr(), bytes.len(), RELIABLE) };

    // SAFETY: `server_peer` is live and `packet` is a live handle this test owns.
    assert_eq!(unsafe { enet_peer_send(link.server_peer, 0, packet) }, 0);

    let mut received = None;
    pump(link.server, &mut link.client, |seen| match seen {
        Seen::Client(EventNoRef::Receive { packet, .. }) => {
            received = Some(packet.data().to_vec());
            true
        }
        _ => false,
    });
    assert_payload(&received.expect("no packet arrived"), 3000);
}
