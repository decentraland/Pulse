namespace Pulse.InterestManagement;

public sealed class SpatialHashAreaOfInterestOptions
{
    public const string SECTION_NAME = "SpatialHashAreaOfInterest";

    public float Tier0Radius { get; set; } = 20f;

    public float Tier1Radius { get; set; } = 50f;

    public float MaxRadius { get; set; } = 100f;

    /// <summary>
    ///     Size of each grid cell in world units. Peers are bucketed by cell.
    /// </summary>
    public float CellSize { get; set; } = 50f;
}
