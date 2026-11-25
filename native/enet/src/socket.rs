use std::io::{self, ErrorKind};
use std::mem::MaybeUninit;
use std::net::{Ipv4Addr, Ipv6Addr, SocketAddr, SocketAddrV4, SocketAddrV6, UdpSocket};
use std::time::Duration;

use rusty_enet::{PacketReceived, Socket, SocketOptions, MTU_MAX};
use socket2::{Domain, Protocol, Type};

/// Datagrams skipped per `receive` call before it reports "nothing received".
const MAX_SKIPPED_DATAGRAMS: usize = 16;

/// Windows `WSAEMSGSIZE`: the datagram was larger than the receive buffer.
#[cfg(windows)]
const WSAEMSGSIZE: i32 = 10040;
/// Windows `WSAENETRESET`: the network dropped the connection.
#[cfg(windows)]
const WSAENETRESET: i32 = 10052;

/// A UDP socket for `rusty_enet`: IPv4 or dual-stack IPv6, with every peer reported as one
/// address form (mapped IPv4 becomes `SocketAddr::V4`).
#[doc(hidden)]
pub struct GlueSocket {
    socket: UdpSocket,
    dual_stack: bool,
}

impl GlueSocket {
    /// Binds `address`; an IPv6 address binds a dual-stack socket. `None` binds a dual-stack
    /// `[::]:0`, or `0.0.0.0:0` when IPv6 is unavailable.
    pub fn bind(address: Option<SocketAddr>) -> io::Result<GlueSocket> {
        match address {
            Some(address) => Self::bind_exact(address),
            None => Self::bind_exact(SocketAddr::V6(SocketAddrV6::new(
                Ipv6Addr::UNSPECIFIED,
                0,
                0,
                0,
            )))
            .or_else(|_| {
                Self::bind_exact(SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::UNSPECIFIED, 0)))
            }),
        }
    }

    fn bind_exact(address: SocketAddr) -> io::Result<GlueSocket> {
        let socket = socket2::Socket::new(
            Domain::for_address(address),
            Type::DGRAM,
            Some(Protocol::UDP),
        )?;
        if address.is_ipv6() {
            socket.set_only_v6(false)?;
        }
        socket.bind(&address.into())?;
        Ok(GlueSocket {
            socket: socket.into(),
            dual_stack: address.is_ipv6(),
        })
    }

    /// Returns the bound address, with the real port when port 0 was requested.
    pub fn local_addr(&self) -> io::Result<SocketAddr> {
        self.socket.local_addr()
    }

    /// Waits until a datagram (or a socket error) is pending, or the timeout elapses.
    pub fn wait_readable(&self, timeout: Duration) -> bool {
        let millis = timeout.as_micros().div_ceil(1000).min(i32::MAX as u128) as i32;
        poll_readable(&self.socket, millis)
    }

    fn to_wire(&self, address: SocketAddr) -> SocketAddr {
        match address {
            SocketAddr::V4(v4) if self.dual_stack => {
                SocketAddr::V6(SocketAddrV6::new(v4.ip().to_ipv6_mapped(), v4.port(), 0, 0))
            }
            other => other,
        }
    }
}

impl Socket for GlueSocket {
    type Address = SocketAddr;
    type Error = io::Error;

    fn init(&mut self, socket_options: SocketOptions) -> Result<(), io::Error> {
        self.socket.set_nonblocking(true)?;
        self.socket.set_broadcast(true)?;
        let socket = socket2::SockRef::from(&self.socket);
        // ENet treats the buffer sizes as hints, so a refusal is not an error.
        let _ = socket.set_recv_buffer_size(socket_options.receive_buffer);
        let _ = socket.set_send_buffer_size(socket_options.send_buffer);
        Ok(())
    }

    fn send(&mut self, address: SocketAddr, buffer: &[u8]) -> Result<usize, io::Error> {
        Ok(self
            .socket
            .send_to(buffer, self.to_wire(address))
            .unwrap_or(0))
    }

    fn receive(
        &mut self,
        buffer: &mut [u8; MTU_MAX],
    ) -> Result<Option<(SocketAddr, PacketReceived)>, io::Error> {
        let socket = socket2::SockRef::from(&self.socket);
        for _ in 0..MAX_SKIPPED_DATAGRAMS {
            // SAFETY: `MaybeUninit<u8>` has the layout of `u8`, and `recv_from` only writes.
            let uninit = unsafe {
                std::slice::from_raw_parts_mut(
                    buffer.as_mut_ptr().cast::<MaybeUninit<u8>>(),
                    MTU_MAX,
                )
            };
            match socket.recv_from(uninit) {
                Ok((length, from)) => {
                    let from = from.as_socket().ok_or_else(|| {
                        io::Error::new(ErrorKind::InvalidData, "datagram without an IP address")
                    })?;
                    return Ok(Some((normalize(from), PacketReceived::Complete(length))));
                }
                Err(err) if err.kind() == ErrorKind::WouldBlock => return Ok(None),
                Err(err) if is_skippable(&err) => continue,
                Err(err) => return Err(err),
            }
        }
        Ok(None)
    }
}

/// Reports a mapped IPv4 peer as `SocketAddr::V4`.
fn normalize(address: SocketAddr) -> SocketAddr {
    match address {
        SocketAddr::V6(v6) => match v6.ip().to_ipv4_mapped() {
            Some(v4) => SocketAddr::V4(SocketAddrV4::new(v4, v6.port())),
            None => SocketAddr::V6(v6),
        },
        v4 => v4,
    }
}

/// Errors that describe one lost datagram rather than a broken socket.
fn is_skippable(err: &io::Error) -> bool {
    if matches!(
        err.kind(),
        ErrorKind::ConnectionReset
            | ErrorKind::ConnectionRefused
            | ErrorKind::HostUnreachable
            | ErrorKind::NetworkUnreachable
    ) {
        return true;
    }
    #[cfg(windows)]
    if matches!(err.raw_os_error(), Some(WSAEMSGSIZE | WSAENETRESET)) {
        return true;
    }
    false
}

#[cfg(unix)]
fn poll_readable(socket: &UdpSocket, millis: i32) -> bool {
    use std::os::fd::AsRawFd;

    let mut fd = libc::pollfd {
        fd: socket.as_raw_fd(),
        events: libc::POLLIN,
        revents: 0,
    };
    // SAFETY: `fd` is a valid, initialized pollfd and the count is 1.
    unsafe { libc::poll(&mut fd, 1, millis) > 0 }
}

#[cfg(windows)]
fn poll_readable(socket: &UdpSocket, millis: i32) -> bool {
    use std::os::windows::io::AsRawSocket;
    use windows_sys::Win32::Networking::WinSock::{WSAPoll, POLLRDNORM, WSAPOLLFD};

    let mut fd = WSAPOLLFD {
        fd: socket.as_raw_socket() as usize,
        events: POLLRDNORM,
        revents: 0,
    };
    // SAFETY: `fd` is a valid, initialized WSAPOLLFD and the count is 1.
    unsafe { WSAPoll(&mut fd, 1, millis) > 0 }
}
