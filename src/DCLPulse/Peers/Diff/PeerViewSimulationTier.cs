namespace Pulse.Peers;

/// <summary>
///     Determines how detailed the data sent about a peer to another peer is
/// </summary>
public readonly struct PeerViewSimulationTier(byte tier) : IEquatable<PeerViewSimulationTier>
{
    public static readonly PeerViewSimulationTier TIER_0 = new (0);
    public static readonly PeerViewSimulationTier TIER_1 = new (1);
    public static readonly PeerViewSimulationTier TIER_2 = new (2);

    public byte Value => tier;

    public bool Equals(PeerViewSimulationTier other) =>
        tier == other.Value;

    public override bool Equals(object? obj) =>
        obj is PeerViewSimulationTier other && Equals(other);

    public override int GetHashCode() =>
        tier;
}
