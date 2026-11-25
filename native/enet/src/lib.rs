//! ENet-CSharp compatible C exports implemented over `rusty_enet`.

mod address;
mod guard;
mod host;
mod library;
mod log;
mod packet;
mod peer;
#[doc(hidden)]
pub mod socket;

pub use address::{
    enet_address_get_ip, enet_address_set_hostname, enet_address_set_ip, from_socket_addr, ip_text,
    to_socket_addr, ENetAddress,
};
pub use host::{
    enet_host_check_events, enet_host_connect, enet_host_create, enet_host_destroy,
    enet_host_flush, enet_host_service, ENetEvent, GlueHost,
};
pub use library::{enet_deinitialize, enet_initialize, enet_linked_version, enet_time_get};
pub use packet::{
    enet_packet_create, enet_packet_create_offset, enet_packet_dispose, enet_packet_get_data,
    enet_packet_get_length, PacketHandle,
};
pub use peer::{
    enet_peer_disconnect, enet_peer_disconnect_later, enet_peer_disconnect_now, enet_peer_get_id,
    enet_peer_get_ip, enet_peer_get_mtu, enet_peer_get_port, enet_peer_get_rtt,
    enet_peer_get_state, enet_peer_ping_interval, enet_peer_reset, enet_peer_send,
    enet_peer_timeout, PeerRecord,
};
