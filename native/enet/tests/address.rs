use std::ffi::{c_char, CStr, CString};
use std::net::{IpAddr, Ipv4Addr, SocketAddr, UdpSocket};
use std::ptr;
use std::time::{Duration, Instant};

use enet::socket::GlueSocket;
use enet::{enet_address_get_ip, enet_address_set_hostname, enet_address_set_ip, ENetAddress};
use rusty_enet::{PacketReceived, Socket, SocketOptions, MTU_MAX};

fn blank() -> ENetAddress {
    ENetAddress {
        ipv6: [0xAA; 16],
        port: 0,
    }
}

fn set_ip(address: &mut ENetAddress, text: &str) -> i32 {
    let text = CString::new(text).unwrap();
    // SAFETY: both pointers are valid for the duration of the call.
    unsafe { enet_address_set_ip(address, text.as_ptr()) }
}

fn set_hostname(address: &mut ENetAddress, text: &str) -> i32 {
    let text = CString::new(text).unwrap();
    // SAFETY: both pointers are valid for the duration of the call.
    unsafe { enet_address_set_hostname(address, text.as_ptr()) }
}

fn get_ip(address: &ENetAddress, capacity: usize) -> Result<String, i32> {
    let mut buffer = vec![0x7Fu8; capacity];
    // SAFETY: the buffer is valid for `capacity` bytes.
    let result =
        unsafe { enet_address_get_ip(address, buffer.as_mut_ptr().cast::<c_char>(), capacity) };
    if result != 0 {
        return Err(result);
    }
    // SAFETY: a successful call wrote a NUL-terminated string into the buffer.
    let text = unsafe { CStr::from_ptr(buffer.as_ptr().cast::<c_char>()) };
    Ok(text.to_str().unwrap().to_owned())
}

#[test]
fn set_ip_v4_is_stored_mapped() {
    let mut address = blank();
    assert_eq!(set_ip(&mut address, "127.0.0.1"), 0);
    let mut expected = [0u8; 16];
    expected[10] = 0xFF;
    expected[11] = 0xFF;
    expected[12..].copy_from_slice(&[127, 0, 0, 1]);
    assert_eq!(address.ipv6, expected);
}

#[test]
fn set_ip_v6_is_stored_as_is() {
    let mut address = blank();
    assert_eq!(set_ip(&mut address, "::1"), 0);
    let mut expected = [0u8; 16];
    expected[15] = 1;
    assert_eq!(address.ipv6, expected);
}

#[test]
fn set_ip_rejects_garbage() {
    let mut address = blank();
    assert_eq!(set_ip(&mut address, "not-an-ip"), -1);
}

#[test]
fn get_ip_prints_mapped_v4_dotted() {
    let mut address = blank();
    assert_eq!(set_ip(&mut address, "10.1.2.3"), 0);
    assert_eq!(get_ip(&address, 64).unwrap(), "10.1.2.3");
}

#[test]
fn get_ip_prints_v6() {
    let mut address = blank();
    assert_eq!(set_ip(&mut address, "::1"), 0);
    assert_eq!(get_ip(&address, 64).unwrap(), "::1");
}

#[test]
fn get_ip_fails_when_buffer_too_small() {
    let mut address = blank();
    assert_eq!(set_ip(&mut address, "10.1.2.3"), 0);
    assert_eq!(get_ip(&address, 4), Err(-1));
    assert_eq!(get_ip(&address, 8), Err(-1));
    assert_eq!(get_ip(&address, 9).unwrap(), "10.1.2.3");
}

#[test]
fn set_hostname_resolves_localhost() {
    let mut address = blank();
    assert_eq!(set_hostname(&mut address, "localhost"), 0);
    let text = get_ip(&address, 64).unwrap();
    assert!(text == "127.0.0.1" || text == "::1", "got {text}");
}

#[test]
fn set_hostname_fails_for_unresolvable() {
    let mut address = blank();
    assert_eq!(set_hostname(&mut address, "no-such-host.invalid"), -1);
}

#[test]
fn set_hostname_accepts_ip_literal() {
    let mut address = blank();
    assert_eq!(set_hostname(&mut address, "192.0.2.7"), 0);
    assert_eq!(get_ip(&address, 64).unwrap(), "192.0.2.7");
}

#[test]
fn null_pointers_return_error() {
    let mut address = blank();
    let text = CString::new("127.0.0.1").unwrap();
    let mut buffer = [0 as c_char; 32];
    // SAFETY: null arguments are the case under test; the other pointers are valid.
    unsafe {
        assert_eq!(enet_address_set_ip(ptr::null_mut(), text.as_ptr()), -1);
        assert_eq!(enet_address_set_ip(&mut address, ptr::null()), -1);
        assert_eq!(
            enet_address_set_hostname(ptr::null_mut(), text.as_ptr()),
            -1
        );
        assert_eq!(enet_address_set_hostname(&mut address, ptr::null()), -1);
        assert_eq!(
            enet_address_get_ip(ptr::null(), buffer.as_mut_ptr(), 32),
            -1
        );
        assert_eq!(enet_address_get_ip(&address, ptr::null_mut(), 32), -1);
    }
}

#[test]
fn socket_binds_v4_and_reports_port() {
    let socket = GlueSocket::bind(Some("127.0.0.1:0".parse().unwrap())).unwrap();
    assert_ne!(socket.local_addr().unwrap().port(), 0);
}

#[test]
fn dual_stack_socket_receives_v4() {
    let mut socket = GlueSocket::bind(Some("[::]:0".parse().unwrap())).unwrap();
    let port = socket.local_addr().unwrap().port();
    let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
    sender.send_to(b"hello", ("127.0.0.1", port)).unwrap();
    assert!(socket.wait_readable(Duration::from_secs(2)));

    let mut buffer = [0u8; MTU_MAX];
    let (from, received) = socket.receive(&mut buffer).unwrap().unwrap();
    assert!(matches!(received, PacketReceived::Complete(5)));
    assert_eq!(&buffer[..5], b"hello");
    assert_eq!(
        from,
        SocketAddr::new(
            IpAddr::V4(Ipv4Addr::LOCALHOST),
            sender.local_addr().unwrap().port()
        )
    );
}

/// Sends `oversized` bytes and then a 100-byte datagram to a fresh socket, and returns what the
/// first `receive` call yields: its length and first byte.
fn receive_after_oversized(oversized: usize) -> (usize, u8) {
    let mut socket = GlueSocket::bind(Some("127.0.0.1:0".parse().unwrap())).unwrap();
    // Non-blocking, as under ENet, so a datagram that never arrives fails the test at the
    // deadline rather than hanging it.
    socket
        .init(SocketOptions {
            receive_buffer: 256 * 1024,
            send_buffer: 256 * 1024,
        })
        .unwrap();
    let port = socket.local_addr().unwrap().port();
    let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
    sender
        .send_to(&vec![0xAA; oversized], ("127.0.0.1", port))
        .unwrap();
    sender.send_to(&[0xBB; 100], ("127.0.0.1", port)).unwrap();

    let deadline = Instant::now() + Duration::from_secs(2);
    let mut buffer = [0u8; MTU_MAX];
    loop {
        assert!(
            Instant::now() < deadline,
            "timed out waiting for the datagram that follows the oversized one"
        );
        socket.wait_readable(Duration::from_millis(50));
        match socket.receive(&mut buffer).unwrap() {
            Some((_, PacketReceived::Complete(length))) => return (length, buffer[0]),
            Some((_, PacketReceived::Partial)) => {
                panic!("the glue skips a truncated datagram itself")
            }
            None => {}
        }
    }
}

#[test]
fn datagram_of_5000_bytes_is_skipped() {
    assert_eq!(receive_after_oversized(5000), (100, 0xBB));
}

#[test]
fn datagram_one_byte_over_mtu_max_is_skipped() {
    assert_eq!(receive_after_oversized(MTU_MAX + 1), (100, 0xBB));
}

#[test]
fn datagram_of_exactly_mtu_max_is_received_whole() {
    let mut socket = GlueSocket::bind(Some("127.0.0.1:0".parse().unwrap())).unwrap();
    let port = socket.local_addr().unwrap().port();
    let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
    let mut payload = vec![0xCC; MTU_MAX];
    payload[MTU_MAX - 1] = 0xDD;
    sender.send_to(&payload, ("127.0.0.1", port)).unwrap();
    assert!(socket.wait_readable(Duration::from_secs(2)));

    let mut buffer = [0u8; MTU_MAX];
    let (_, received) = socket.receive(&mut buffer).unwrap().unwrap();
    assert!(matches!(received, PacketReceived::Complete(MTU_MAX)));
    assert_eq!(buffer[0], 0xCC);
    assert_eq!(buffer[MTU_MAX - 1], 0xDD);
}

#[test]
fn wait_readable_times_out() {
    let socket = GlueSocket::bind(Some("127.0.0.1:0".parse().unwrap())).unwrap();
    let started = Instant::now();
    assert!(!socket.wait_readable(Duration::from_millis(20)));
    assert!(started.elapsed() >= Duration::from_millis(15));
}

#[test]
fn wait_readable_wakes_on_datagram() {
    let socket = GlueSocket::bind(Some("127.0.0.1:0".parse().unwrap())).unwrap();
    let port = socket.local_addr().unwrap().port();
    let sender = UdpSocket::bind("127.0.0.1:0").unwrap();
    sender.send_to(b"x", ("127.0.0.1", port)).unwrap();
    assert!(socket.wait_readable(Duration::from_secs(2)));
}
