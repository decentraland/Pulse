using Microsoft.Extensions.Options;

namespace Pulse.InterestManagement;

/// <summary>
///     Maps an announced parcel set to the deduped <see cref="SpatialGrid" /> cell keys
///     covering it. Computed once per scene-listener handshake; immutable thereafter.
///     Each 16m parcel overlaps 1–4 of the larger grid cells. The closed max corner may
///     over-cover one neighboring cell when a parcel edge lands exactly on a cell boundary —
///     harmless, the simulation filters candidates parcel-exact.
/// </summary>
public sealed class SceneListenerCellMapper(
    ParcelEncoder parcelEncoder,
    SpatialGrid grid,
    IOptions<ParcelEncoderOptions> parcelOptions)
{
    private readonly int parcelSize = parcelOptions.Value.ParcelSize;

    public long[] ComputeCellKeys(IReadOnlyCollection<int> parcelIndices)
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
