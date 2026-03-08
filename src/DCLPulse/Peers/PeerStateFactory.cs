namespace Pulse.Peers;

public class PeerStateFactory(PeerOptions options)
{
    private readonly int historyCapacity = options.SnapshotHistoryCapacity;

    /// <summary>
    ///     TODO add pooling
    /// </summary>
    public PeerState Create() =>
        new (PeerConnectionState.PENDING_AUTH, new PeerSnapshotHistory(historyCapacity));
}
