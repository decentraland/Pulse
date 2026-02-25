namespace Pulse.Peers;

/// <summary>
///     The entry point to the peer state stored on the server
/// </summary>
public class PeerState
{
    public string? WalletId { get; private set; }

    public PeerConnectionState ConnectionState { get; private set; }
}
