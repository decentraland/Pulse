using Microsoft.Extensions.Options;

namespace Pulse.InterestManagement;

/// <summary>
///     Maps an announced AoI to the deduped <see cref="SpatialGrid" /> cell keys covering it.
///     Computed once per scene-listener announcement; immutable thereafter.
///     Each 16m parcel overlaps 1–4 of the larger grid cells. The closed max corner may
///     over-cover one neighboring cell when a parcel edge lands exactly on a cell boundary,
///     and realms share one grid coordinate space so their cells overlap outright — both are
///     harmless, the simulation filters candidates realm- and parcel-exact.
/// </summary>
public sealed class SceneListenerCellMapper(
    ParcelEncoder parcelEncoder,
    SpatialGrid grid,
    IOptions<ParcelEncoderOptions> parcelOptions)
{
    private readonly int parcelSize = parcelOptions.Value.ParcelSize;

    public long[] ComputeCellKeys(Dictionary<string, HashSet<int>> parcelsByRealm) =>
        ComputeCellKeys(parcelsByRealm.Values.SelectMany(parcels => parcels));

    public long[] ComputeCellKeys(IEnumerable<int> parcelIndices)
    {
        var keys = new HashSet<long>();

        foreach (int index in parcelIndices)
        {
            parcelEncoder.Decode(index, out int px, out int pz);
            float minX = px * parcelSize;
            float minZ = pz * parcelSize;

            keys.Add(grid.ComputeCellKey(minX, minZ));
            keys.Add(grid.ComputeCellKey(minX + parcelSize, minZ));
            keys.Add(grid.ComputeCellKey(minX, minZ + parcelSize));
            keys.Add(grid.ComputeCellKey(minX + parcelSize, minZ + parcelSize));
        }

        var result = new long[keys.Count];
        keys.CopyTo(result);
        return result;
    }
}
