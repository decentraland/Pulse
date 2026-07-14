namespace Pulse.Metrics;

/// <summary>
///     Lock-free fixed-bucket histogram. <see cref="Record" /> is safe from any thread —
///     a linear bucket scan plus two Interlocked adds, cheap enough for the simulation
///     hot path. <see cref="Snapshot" /> copies the counters into an immutable
///     <see cref="HistogramSnapshot" /> for percentile computation downstream.
///     Bucket bounds are inclusive upper edges; values above the last bound land in the
///     +Inf overflow bucket.
///     Deliberate trade-off: all recording threads share one <c>counts</c> array, so adjacent
///     longs can false-share a cache line. At current scale that's fine; if profiling ever shows
///     cache-line contention the fix is per-worker sharding merged at <see cref="Snapshot" />.
/// </summary>
public sealed class BucketHistogram(long[] upperBounds)
{
    private readonly long[] counts = new long[upperBounds.Length + 1];
    private long sum;

    public void Record(long value)
    {
        var i = 0;

        while (i < upperBounds.Length && value > upperBounds[i])
            i++;

        Interlocked.Increment(ref counts[i]);
        Interlocked.Add(ref sum, value);
    }

    public HistogramSnapshot Snapshot()
    {
        var copy = new long[counts.Length];
        long total = 0;

        for (var i = 0; i < counts.Length; i++)
        {
            copy[i] = Interlocked.Read(ref counts[i]);
            total += copy[i];
        }

        return new HistogramSnapshot
        {
            UpperBounds = upperBounds,
            Counts = copy,
            Count = total,
            Sum = Interlocked.Read(ref sum),
        };
    }
}
