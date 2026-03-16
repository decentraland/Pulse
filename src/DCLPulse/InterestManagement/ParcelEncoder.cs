using Microsoft.Extensions.Options;
using System.Numerics;

namespace Pulse.InterestManagement;

public class ParcelEncoder
{
    private readonly int minX;
    private readonly int minZ;
    private readonly int width;
    private readonly int parcelSize;

    public ParcelEncoder(IOptions<ParcelEncoderOptions> optionsContainer)
    {
        ParcelEncoderOptions options = optionsContainer.Value;
        int padding = options.Padding;
        minX = options.MinParcelX - padding;
        minZ = options.MinParcelZ - padding;
        int maxX = options.MaxParcelX + padding;
        width = maxX - minX + 1;
        parcelSize =  options.ParcelSize;
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
