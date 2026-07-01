using DCL.WebTransport;

namespace Pulse.Transport;

/// <summary>
///     Adapts the native <see cref="WebTransportHost" /> to <see cref="IWebTransportHost" />. The
///     span-taking native sends receive the already-framed byte arrays produced by the
///     channel-semantics layer.
/// </summary>
public sealed class WebTransportHostAdapter(WebTransportHost host) : IWebTransportHost
{
    public bool TryService(uint timeoutMs, out WebTransportEvent ev) =>
        host.TryService(timeoutMs, out ev);

    public bool SendStream(ulong peerId, byte[] data) =>
        host.SendStream(peerId, data);

    public bool SendDatagram(ulong peerId, byte[] data) =>
        host.SendDatagram(peerId, data);

    public bool Disconnect(ulong peerId, uint reason) =>
        host.Disconnect(peerId, reason);

    public void Dispose() =>
        host.Dispose();
}
