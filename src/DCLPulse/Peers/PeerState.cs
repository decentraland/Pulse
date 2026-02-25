namespace Pulse.Peers;

/// <summary>
///     The entry point to the peer state stored on the server
/// </summary>
public class PeerState
{
    public PeerConnectionState ConnectionState { get; private set; }
}
