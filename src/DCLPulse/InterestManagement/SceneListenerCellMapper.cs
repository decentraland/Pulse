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
///     exactly on a cell boundary, and realms share one grid coordinate space so their cells
///     overlap outright — both are harmless, the simulation filters candidates realm- and
///     parcel-exact.
/// </summary>
public sealed class SceneListenerCellMapper(SpatialGrid grid, IOptions<ParcelEncoderOptions> parcelOptions)
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
        int minCellX = grid.CellCoord(minX * parcelSize);
        int minCellZ = grid.CellCoord(minZ * parcelSize);
        int maxCellX = grid.CellCoord((maxX + 1) * parcelSize);
        int maxCellZ = grid.CellCoord((maxZ + 1) * parcelSize);

        for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
            for (int cellX = minCellX; cellX <= maxCellX; cellX++)
                keys.Add(SpatialGrid.PackKey(cellX, cellZ));
    }
}
