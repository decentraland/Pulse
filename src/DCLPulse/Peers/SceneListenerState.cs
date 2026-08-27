namespace Pulse.Peers;

/// <summary>
///     Immutable scene-listener descriptor stamped onto <see cref="PeerState" /> at handshake.
///     A peer carrying this never publishes snapshots (invisible to players) and observes a
///     parcel set per realm instead of a radius around its own position. The descriptor itself
///     never changes: a <c>SceneListenerUpdate</c> swaps in a whole new one, so a reader that has
///     already resolved it always sees a consistent AoI.
///     <para />
///     A listener may observe several realms at once — an authoritative server cohosting scenes
///     from several worlds does — so a parcel is only meaningful together with its realm: every
///     world numbers its parcels from 0,0, and two cohosted worlds would otherwise collide.
/// </summary>
public sealed class SceneListenerState(Dictionary<string, HashSet<int>> parcelsByRealm, long[] cellKeys)
{
    /// <summary>Announced parcel set per realm — the parcel-exact visibility filter.</summary>
    public Dictionary<string, HashSet<int>> ParcelsByRealm { get; } = parcelsByRealm;

    /// <summary>
    ///     Deduped SpatialGrid cell keys covering every announced realm's parcels. The grid is one
    ///     global coordinate space, so realms overlap in it — that only ever over-covers, and
    ///     candidates are filtered realm- and parcel-exact by the simulation, so the cost is a
    ///     lookup, never correctness.
    /// </summary>
    public long[] CellKeys { get; } = cellKeys;

    /// <summary>
    ///     Total announced parcels across all realms — logging and metrics only. Summed once here
    ///     rather than per read: the reads are log-statement arguments, which are evaluated whether
    ///     or not the level is enabled.
    /// </summary>
    public int ParcelCount { get; } = SumParcels(parcelsByRealm);

    /// <summary>Whether a subject standing in <paramref name="parcel" /> of <paramref name="realm" /> is observed.</summary>
    public bool Observes(string? realm, int parcel) =>
        realm != null && ParcelsByRealm.TryGetValue(realm, out HashSet<int>? parcels) && parcels.Contains(parcel);

    private static int SumParcels(Dictionary<string, HashSet<int>> parcelsByRealm)
    {
        int total = 0;

        foreach (HashSet<int> parcels in parcelsByRealm.Values)
            total += parcels.Count;

        return total;
    }
}
