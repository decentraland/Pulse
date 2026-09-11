using Pulse.InterestManagement;
using Pulse.Peers;
using System.Numerics;

namespace DCLPulseTests;

/// <summary>
///     Single-position occupancy lookups against <see cref="RealmSpatialGrids" />. Production callers
///     walk a whole cell neighbourhood and so compose cell coordinates themselves; only assertions want
///     to ask "who is in this realm at this exact spot".
/// </summary>
internal static class RealmGridQueries
{
    /// <summary>
    ///     The occupants of the cell containing <paramref name="position" /> in
    ///     <paramref name="realm" />, or null when the realm holds no peers or the cell is empty.
    /// </summary>
    public static HashSet<PeerIndex>? PeersAt(this RealmSpatialGrids grids, string realm, Vector3 position)
    {
        SpatialGrid? grid = grids.GetGrid(realm);

        if (grid is null) return null;

        grids.CellCoords(position, out int x, out int z);

        return grid.GetPeers(SpatialGrid.PackKey(x, z));
    }

    /// <summary>
    ///     How many realms currently hold at least one peer — i.e. how many grids are live.
    /// </summary>
    public static int LiveRealmCount(this RealmSpatialGrids grids)
    {
        var count = 0;

        foreach (RealmSpatialGrids.RealmGrid _ in grids.GetRealmGrids())
            count++;

        return count;
    }
}
