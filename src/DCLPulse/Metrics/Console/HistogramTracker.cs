namespace Pulse.Metrics.Console;

/// <summary>
///     Adapts a cumulative <see cref="HistogramSnapshot" /> into <see cref="RateStats" />
///     for the dashboard: PerSec = recordings/s, Window = value-distribution percentiles
///     over the delta since the previous snapshot, Lifetime = percentiles over the
///     cumulative buckets. Unlike RateTracker, the percentiles here describe the measured
///     values (ms/µs), not the rate. Not thread-safe — used only from the dashboard update.
/// </summary>
internal sealed class HistogramTracker
{
    private long prevCount;
    private long[]? prevCounts;

    public RateStats Update(in HistogramSnapshot snap, double elapsedSec)
    {
        if (snap.Counts == null)
            return default(RateStats);

        double rate = (snap.Count - prevCount) / elapsedSec;

        var delta = new long[snap.Counts.Length];

        for (var i = 0; i < snap.Counts.Length; i++)
            delta[i] = snap.Counts[i] - (prevCounts != null && i < prevCounts.Length ? prevCounts[i] : 0);

        var window = new HistogramSnapshot
        {
            UpperBounds = snap.UpperBounds,
            Counts = delta,
            Count = snap.Count - prevCount,
        };

        prevCount = snap.Count;
        prevCounts = snap.Counts;

        return new RateStats
        {
            PerSec = rate,
            Window = ToPercentileStats(in window),
            Lifetime = ToPercentileStats(in snap),
        };
    }

    private static PercentileStats ToPercentileStats(in HistogramSnapshot histogram) =>
        new ()
        {
            P50 = histogram.Percentile(0.50),
            P95 = histogram.Percentile(0.95),
            P99 = histogram.Percentile(0.99),
        };
}
