namespace Pulse.Peers;

/// <summary>
///     Determines how detailed the data sent about a peer to another peer is
/// </summary>
public readonly struct PeerViewSimulationTier(byte tier)
{
    public static readonly PeerViewSimulationTier TIER_0 = new (0);
}
