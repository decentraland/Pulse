namespace Pulse.Peers;

/// <summary>
///     The entry point to the peer state stored on the server
/// </summary>
public class PeerState(PeerConnectionState connectionState)
{
    public string? WalletId { get; set; }

    public PeerConnectionState ConnectionState { get; set; } = connectionState;

    public PeerTransportState TransportState { get; set; }

    /// <summary>
    ///     Pending resync requests keyed by subject. Allocated on first resync request.
    ///     Dictionary because the peer can send multiple resync requests for the same subject
    ///     between simulation ticks — only the latest known_seq is kept.
    /// </summary>
    public Dictionary<PeerIndex, uint>? ResyncRequests { get; set; }
}
