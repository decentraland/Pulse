//! ENet-CSharp compatible C exports implemented over `rusty_enet`.

mod guard;
mod library;

pub use library::{enet_deinitialize, enet_initialize, enet_linked_version, enet_time_get};
