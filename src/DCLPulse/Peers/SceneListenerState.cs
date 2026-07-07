namespace Pulse.Peers;

/// <summary>
///     Immutable scene-listener descriptor stamped onto <see cref="PeerState" /> at handshake.
///     A peer carrying this never publishes snapshots (invisible to players) and observes a
///     fixed parcel set instead of a radius around its own position. Changing the set requires
///     reconnecting.
/// </summary>
public sealed class SceneListenerState(string realm, HashSet<int> parcels, long[] cellKeys)
{
    public string Realm { get; } = realm;

    /// <summary>Announced parcel set — the parcel-exact visibility filter.</summary>
    public HashSet<int> Parcels { get; } = parcels;

    /// <summary>
    ///     Deduped SpatialGrid cell keys covering the parcel set. May over-cover one
    ///     neighboring cell at exact boundaries — candidates are filtered parcel-exact
    ///     by the simulation, so over-coverage costs a lookup, never correctness.
    /// </summary>
    public long[] CellKeys { get; } = cellKeys;
}
