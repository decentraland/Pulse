namespace Pulse.InterestManagement;

public sealed class SpatialAreaOfInterestOptions
{
    public const string SECTION_NAME = "SpatialAreaOfInterest";

    /// <summary>
    ///     Subjects within this radius receive TIER_0 (every tick).
    /// </summary>
    public float Tier0Radius { get; set; } = 20f;

    /// <summary>
    ///     Subjects within this radius (but beyond Tier0) receive TIER_1 (every 2nd tick).
    /// </summary>
    public float Tier1Radius { get; set; } = 50f;

    /// <summary>
    ///     Maximum visibility radius. Subjects beyond this are invisible.
    /// </summary>
    public float MaxRadius { get; set; } = 100f;
}
