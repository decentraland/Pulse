using Microsoft.Extensions.Options;

namespace Pulse.InterestManagement;

/// <summary>
///     Maps an announced scene-listener AoI to the deduped <see cref="SpatialGrid" /> cell keys
///     covering it, one inclusive parcel rect at a time. Computed once per announcement;
///     immutable thereafter.
///     <para />
///     A rect's cover is the contiguous cell range between its two corners, so the work is
///     O(covered cells) rather than O(parcels): a 64x64 rect covers 121 cells, not 4096 parcels
///     times four corners each. The range has no gaps because a parcel is smaller than a grid
///     cell, so advancing one parcel moves the cell coordinate by at most one — the same
///     assumption a per-parcel corner walk already relies on to cover a parcel with four probes.
///     <para />
///     The closed max corner may over-cover one neighbouring cell when a parcel edge lands
///     exactly on a cell boundary — harmless, the simulation filters candidates parcel-exact.
///     <para />
///     Cell keys carry no realm, deliberately: every realm numbers its cells in the same coordinate
///     space, so one covering set serves a multi-realm announcement. Probing it against a single
///     realm's grid is what keeps the realms apart.
/// </summary>
public sealed class SceneListenerCellMapper(RealmSpatialGrids realmGrids, IOptions<ParcelEncoderOptions> parcelOptions)
{
    private readonly int parcelSize = parcelOptions.Value.ParcelSize;

    /// <summary>
    ///     Adds every grid cell key covering the inclusive parcel rect to <paramref name="keys" />.
    ///     Coordinates must already be bounds-checked; this does no validation of its own.
    /// </summary>
    public void AddCoveringCells(HashSet<long> keys, int minX, int minZ, int maxX, int maxZ)
    {
        // The rect's world extent is closed on the max corner: parcel maxX spans up to, and
        // including, the first point of parcel maxX + 1.
        int minCellX = realmGrids.CellCoord(minX * parcelSize);
        int minCellZ = realmGrids.CellCoord(minZ * parcelSize);
        int maxCellX = realmGrids.CellCoord((maxX + 1) * parcelSize);
        int maxCellZ = realmGrids.CellCoord((maxZ + 1) * parcelSize);

        for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
            for (int cellX = minCellX; cellX <= maxCellX; cellX++)
                keys.Add(SpatialGrid.PackKey(cellX, cellZ));
    }
}
