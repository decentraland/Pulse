namespace Pulse.Peers;

public class PeerStateFactory
{
    /// <summary>
    ///     TODO add pooling
    /// </summary>
    public PeerState Create() =>
        new (PeerConnectionState.PENDING_AUTH);
}
