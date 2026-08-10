using DCL.WebTransport;

namespace Pulse.Transport;

/// <summary>
///     Adapts the native <see cref="WebTransportHost" /> to <see cref="IWebTransportHost" />. Sends are
///     a zero-copy pass-through — the framed span goes straight to the span-taking native calls.
/// </summary>
public sealed class WebTransportHostAdapter(WebTransportHost host) : IWebTransportHost
{
    public bool TryService(uint timeoutMs, out WebTransportEvent ev) =>
        host.TryService(timeoutMs, out ev);

    public bool SendStream(ulong peerId, ReadOnlySpan<byte> data) =>
        host.SendStream(peerId, data);

    public bool SendDatagram(ulong peerId, ReadOnlySpan<byte> data) =>
        host.SendDatagram(peerId, data);

    public bool Disconnect(ulong peerId, uint reason) =>
        host.Disconnect(peerId, reason);

    public void Dispose() =>
        host.Dispose();
}
