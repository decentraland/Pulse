using DCL.WebTransport;

namespace Pulse.Transport;

/// <summary>
///     Test seam over the native <see cref="WebTransportHost" />. Driven from a single dedicated
///     thread (the ENet model): drain one event per <see cref="TryService" />, send on the same
///     thread between drains. Substituted in unit tests so <see cref="WebTransportHostedService" />
///     can be exercised without the native library. Send payloads are <c>byte[]</c> (already framed
///     by the channel-semantics layer) so they are cheap to assert on in tests.
/// </summary>
public interface IWebTransportHost : IDisposable
{
    /// <summary>Block up to <paramref name="timeoutMs" /> for the next event; false on timeout.</summary>
    bool TryService(uint timeoutMs, out WebTransportEvent ev);

    /// <summary>Queue framed bytes on the peer's reliable bidi stream. False if the peer is gone.</summary>
    bool SendStream(ulong peerId, byte[] data);

    /// <summary>Send a framed unreliable datagram to the peer. False on failure (gone / too large).</summary>
    bool SendDatagram(ulong peerId, byte[] data);

    /// <summary>Close the peer's session with an application error code. False if gone.</summary>
    bool Disconnect(ulong peerId, uint reason);
}
