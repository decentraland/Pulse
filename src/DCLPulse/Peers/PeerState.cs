namespace Pulse.Peers;

/// <summary>
///     The entry point to the peer state stored on the server
/// </summary>
public class PeerState(PeerConnectionState connectionState, PeerSnapshotHistory snapshotHistory)
{
    public string? WalletId { get; set; }

    public PeerConnectionState ConnectionState { get; set; } = connectionState;

    public PeerSnapshotHistory SnapshotHistory = snapshotHistory;

    public PeerTransportState TransportState { get; set; }
}
