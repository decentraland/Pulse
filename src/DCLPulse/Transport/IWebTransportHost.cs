using DCL.WebTransport;

namespace Pulse.Transport;

/// <summary>
///     Test seam over the native <see cref="WebTransportHost" />. Driven from a single dedicated
///     thread (the ENet model): drain one event per <see cref="TryService" />, send on the same
///     thread between drains. Substituted in unit tests so <see cref="WebTransportHostedService" />
///     can be exercised without the native library. Sends take a read-only span to match the native
///     calls, letting the channel-semantics layer frame into a reused or stack buffer without allocating.
///     NSubstitute can't observe a ref-struct argument, so the tests use a small recording fake.
/// </summary>
public interface IWebTransportHost : IDisposable
{
    /// <summary>Block up to <paramref name="timeoutMs" /> for the next event; false on timeout.</summary>
    bool TryService(uint timeoutMs, out WebTransportEvent ev);

    /// <summary>Queue <paramref name="data" /> on the peer's reliable bidi stream. False if the peer is gone.</summary>
    bool SendStream(ulong peerId, ReadOnlySpan<byte> data);

    /// <summary>Send <paramref name="data" /> as an unreliable datagram. False on failure (gone / too large).</summary>
    bool SendDatagram(ulong peerId, ReadOnlySpan<byte> data);

    /// <summary>Close the peer's session with an application error code. False if gone.</summary>
    bool Disconnect(ulong peerId, uint reason);
}
