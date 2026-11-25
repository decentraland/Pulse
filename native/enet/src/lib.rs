//! ENet-CSharp compatible C exports implemented over `rusty_enet`.

mod address;
mod guard;
mod library;
#[doc(hidden)]
pub mod socket;

pub use address::{
    enet_address_get_ip, enet_address_set_hostname, enet_address_set_ip, from_socket_addr, ip_text,
    to_socket_addr, ENetAddress,
};
pub use library::{enet_deinitialize, enet_initialize, enet_linked_version, enet_time_get};
