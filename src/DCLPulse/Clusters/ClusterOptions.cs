namespace Pulse.Clusters;

public sealed class ClusterOptions
{
    public const string SECTION_NAME = "Clusters";

    /// <summary>
    ///     Feature flag. When false no tracker thread starts and no cluster state is derived.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Cadence of the clustering pass. Also the upper bound on how stale a published
    ///     assignment or a stats read can be.
    /// </summary>
    public int PassIntervalMs { get; set; } = 1000;

    /// <summary>
    ///     Consecutive passes that must agree on a new assignment before it is published. Temporal
    ///     hysteresis in place of archipelago's join/leave distance bands, absorbing the cell-boundary
    ///     noise of clustering on grid cells rather than peer-pair distances. Bypassed for first
    ///     assignment, teleport, realm change and cluster deletion.
    /// </summary>
    public int DwellPasses { get; set; } = 3;

    /// <summary>
    ///     Prefix for generated cluster IDs, which are otherwise a monotonic counter.
    /// </summary>
    public string IdPrefix { get; set; } = "C";
}
