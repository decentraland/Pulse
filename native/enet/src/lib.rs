//! ENet-CSharp compatible C exports implemented over `rusty_enet`.

mod address;
mod guard;
mod host;
mod library;
mod peer;
#[doc(hidden)]
pub mod socket;

pub use address::{
    enet_address_get_ip, enet_address_set_hostname, enet_address_set_ip, from_socket_addr, ip_text,
    to_socket_addr, ENetAddress,
};
pub use host::{
    enet_host_check_events, enet_host_connect, enet_host_create, enet_host_destroy,
    enet_host_flush, enet_host_service, ENetEvent, GlueHost, PacketHandle,
};
pub use library::{enet_deinitialize, enet_initialize, enet_linked_version, enet_time_get};
pub use peer::{
    enet_peer_get_id, enet_peer_get_ip, enet_peer_get_mtu, enet_peer_get_port, enet_peer_get_rtt,
    enet_peer_get_state, enet_peer_ping_interval, enet_peer_timeout, PeerRecord,
};
