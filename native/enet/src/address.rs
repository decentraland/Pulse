use std::ffi::CStr;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV4, SocketAddrV6, ToSocketAddrs};
use std::os::raw::{c_char, c_int};

use crate::guard::guard;

/// ENet-CSharp's address: 16 IPv6 bytes and a port. IPv4 is stored as `::ffff:a.b.c.d`.
#[repr(C)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ENetAddress {
    pub ipv6: [u8; 16],
    pub port: u16,
}

/// Converts to a socket address; a mapped IPv4 address becomes `SocketAddr::V4`.
#[doc(hidden)]
pub fn to_socket_addr(a: &ENetAddress) -> SocketAddr {
    let ip = Ipv6Addr::from(a.ipv6);
    match ip.to_ipv4_mapped() {
        Some(v4) => SocketAddr::V4(SocketAddrV4::new(v4, a.port)),
        None => SocketAddr::V6(SocketAddrV6::new(ip, a.port, 0, 0)),
    }
}

/// Converts from a socket address; IPv4 is stored mapped.
#[doc(hidden)]
pub fn from_socket_addr(s: SocketAddr) -> ENetAddress {
    let ip = match s.ip() {
        IpAddr::V4(v4) => v4.to_ipv6_mapped(),
        IpAddr::V6(v6) => v6,
    };
    ENetAddress {
        ipv6: ip.octets(),
        port: s.port(),
    }
}

/// Formats the IP in dotted form for IPv4 and mapped IPv4, and as IPv6 text otherwise.
#[doc(hidden)]
pub fn ip_text(s: SocketAddr) -> String {
    match s.ip() {
        IpAddr::V4(v4) => v4.to_string(),
        IpAddr::V6(v6) => match v6.to_ipv4_mapped() {
            Some(v4) => v4.to_string(),
            None => v6.to_string(),
        },
    }
}

fn store_ip(address: &mut ENetAddress, ip: IpAddr) {
    address.ipv6 = match ip {
        IpAddr::V4(v4) => v4.to_ipv6_mapped(),
        IpAddr::V6(v6) => v6,
    }
    .octets();
}

/// Parses an IP literal: text without `:` is IPv4, text with `:` is IPv6.
fn parse_ip(text: &str) -> Option<IpAddr> {
    if text.contains(':') {
        text.parse::<Ipv6Addr>().ok().map(IpAddr::V6)
    } else {
        text.parse::<Ipv4Addr>().ok().map(IpAddr::V4)
    }
}

/// Parses an IP literal into `address`, leaving the port untouched. Returns 0 or -1.
///
/// # Safety
///
/// `address` must be null or valid for writes, and `ip` must be null or a valid NUL-terminated
/// string.
#[no_mangle]
pub unsafe extern "C" fn enet_address_set_ip(
    address: *mut ENetAddress,
    ip: *const c_char,
) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees validity; null is rejected by `as_mut`.
        let address = unsafe { address.as_mut() }?;
        if ip.is_null() {
            return None;
        }
        // SAFETY: `ip` is non-null and the caller guarantees it is NUL-terminated.
        let text = unsafe { CStr::from_ptr(ip) }.to_str().ok()?;
        store_ip(address, parse_ip(text)?);
        Some(0)
    })
}

/// Resolves a host name (or IP literal) into `address`, using the first result and leaving the
/// port untouched. Returns 0 or -1.
///
/// # Safety
///
/// `address` must be null or valid for writes, and `name` must be null or a valid
/// NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn enet_address_set_hostname(
    address: *mut ENetAddress,
    name: *const c_char,
) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees validity; null is rejected by `as_mut`.
        let address = unsafe { address.as_mut() }?;
        if name.is_null() {
            return None;
        }
        // SAFETY: `name` is non-null and the caller guarantees it is NUL-terminated.
        let text = unsafe { CStr::from_ptr(name) }.to_str().ok()?;
        let ip = match parse_ip(text) {
            Some(ip) => ip,
            None => (text, 0).to_socket_addrs().ok()?.next()?.ip(),
        };
        store_ip(address, ip);
        Some(0)
    })
}

/// Writes the address as a NUL-terminated string. Returns -1 when `ip_length` cannot hold the
/// text and its terminator.
///
/// # Safety
///
/// `address` must be null or valid for reads, and `ip` must be null or valid for `ip_length`
/// bytes of writes.
#[no_mangle]
pub unsafe extern "C" fn enet_address_get_ip(
    address: *const ENetAddress,
    ip: *mut c_char,
    ip_length: usize,
) -> c_int {
    guard(-1, || {
        // SAFETY: the caller guarantees validity; null is rejected by `as_ref`.
        let address = unsafe { address.as_ref() }?;
        // SAFETY: the caller guarantees `ip` is null or valid for `ip_length` bytes of writes.
        unsafe { write_ip(&ip_text(to_socket_addr(address)), ip, ip_length) }
    })
}

/// Writes `text` into `ip` as a NUL-terminated string. Returns `Some(0)`, or `None` when `ip` is
/// null or `ip_length` cannot hold the text and its terminator.
///
/// # Safety
///
/// `ip` must be null or valid for `ip_length` bytes of writes.
pub(crate) unsafe fn write_ip(text: &str, ip: *mut c_char, ip_length: usize) -> Option<c_int> {
    if ip.is_null() {
        return None;
    }
    let bytes = text.as_bytes();
    if bytes.len() >= ip_length {
        return None;
    }
    // SAFETY: `ip` is valid for `ip_length` bytes and `bytes.len() + 1 <= ip_length`.
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), ip.cast::<u8>(), bytes.len());
        *ip.add(bytes.len()) = 0;
    }
    Some(0)
}
