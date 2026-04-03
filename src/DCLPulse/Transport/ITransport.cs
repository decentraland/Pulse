using Pulse.Peers;

namespace Pulse.Transport;

/// <summary>
///     Abstraction for the transport in case it's used actively from other services
/// </summary>
public interface ITransport
{
    public void Disconnect(PeerIndex pi, DisconnectReason reason);
}
