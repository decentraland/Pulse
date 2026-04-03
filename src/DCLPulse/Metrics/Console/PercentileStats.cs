namespace Pulse.Metrics.Console;

/// <summary>
///     P50/P95/P99 percentile triplet for a single time window.
/// </summary>
public readonly record struct PercentileStats
{
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
}
