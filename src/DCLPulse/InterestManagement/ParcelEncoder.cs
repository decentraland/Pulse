using Microsoft.Extensions.Options;
using System.Numerics;

namespace Pulse.InterestManagement;

public class ParcelEncoder
{
    private readonly int minX;
    private readonly int minZ;
    private readonly int width;
    private readonly int height;
    private readonly int parcelSize;

    public ParcelEncoder(IOptions<ParcelEncoderOptions> optionsContainer)
    {
        ParcelEncoderOptions options = optionsContainer.Value;
        int padding = options.Padding;
        minX = options.MinParcelX - padding;
        minZ = options.MinParcelZ - padding;
        int maxX = options.MaxParcelX + padding;
        int maxZ = options.MaxParcelZ + padding;
        width = maxX - minX + 1;
        height = maxZ - minZ + 1;
        parcelSize =  options.ParcelSize;
    }

    /// <summary>
    ///     Maximum valid index (exclusive) — <c>width * height</c>. A parcel index from the
    ///     client is valid iff <c>0 &lt;= index &lt; MaxIndexExclusive</c>.
    /// </summary>
    public int MaxIndexExclusive => width * height;

    public bool IsValidIndex(int index) => (uint)index < (uint)MaxIndexExclusive;

    public int EncodeFromGlobalPosition(Vector3 globalPosition, out Vector3 localPosition)
    {
        int x = (int)MathF.Floor(globalPosition.X / parcelSize);
        int z = (int)MathF.Floor(globalPosition.Z / parcelSize);
        localPosition = new Vector3(globalPosition.X - (x * parcelSize), globalPosition.Y, globalPosition.Z - (z * parcelSize));
        return Encode(x, z);
    }

    public int Encode(int x, int z) =>
        x - minX + ((z - minZ) * width);

    public void Decode(int index, out int x, out int z)
    {
        x = (index % width) + minX;
        z = (index / width) + minZ;
    }

    public Vector3 DecodeToGlobalPosition(int index, Vector3 localPosition)
    {
        Decode(index, out int x, out int z);

        return new Vector3((x * parcelSize) + localPosition.X,
            localPosition.Y,
            (z * parcelSize) + localPosition.Z);
    }
}
