using System.Numerics;

namespace PulseTestClient;

public class ParcelEncoder(int minX, int minZ, int maxParcelX, int padding, int parcelSize)
{
    private readonly int minX = minX - padding;
    private readonly int minZ = minZ - padding;
    private readonly int width = (maxParcelX + padding) - (minX - padding) + 1;

    public int Encode(int x, int z) =>
        x - minX + (z - minZ) * width;

    public int EncodeGlobalPosition(Vector3 globalPosition, out Vector3 relativePosition)
    {
        var parcelX = (int) Math.Floor(globalPosition.X / parcelSize);
        var parcelZ = (int) Math.Floor(globalPosition.Z / parcelSize);

        relativePosition = new Vector3(
            globalPosition.X - parcelX * parcelSize,
            globalPosition.Y,
            globalPosition.Z - parcelZ * parcelSize
        );

        return Encode(parcelX, parcelZ);
    }

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
