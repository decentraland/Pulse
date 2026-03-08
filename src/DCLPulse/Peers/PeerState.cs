namespace Pulse.Peers;

/// <summary>
///     The entry point to the peer state stored on the server
/// </summary>
public class PeerState(PeerConnectionState connectionState)
{
    public string? WalletId { get; set; }

    public PeerConnectionState ConnectionState { get; set; } = connectionState;

    public PeerTransportState TransportState { get; set; }
}
