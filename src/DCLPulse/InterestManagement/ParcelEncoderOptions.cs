namespace Pulse.InterestManagement;

public class ParcelEncoderOptions
{
    public const string SECTION_NAME = "ParcelEncoder";

    public int MinParcelX { get; set; } = -150;
    public int MinParcelZ { get; set; } = -150;
    public int MaxParcelX { get; set; } = 163;
    public int MaxParcelZ { get; set; } = 158;
    public int Padding { get; set; } = 2;
    public int ParcelSize { get; set; } = 16;
}
