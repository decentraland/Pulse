namespace Pulse.Metrics;

/// <summary>
///     Bundles current rate with rolling-window and lifetime percentiles for a single metric.
/// </summary>
public readonly record struct RateStats
{
    public double PerSec { get; init; }
    public PercentileStats Window { get; init; }
    public PercentileStats Lifetime { get; init; }
}
