namespace Pulse.Metrics;

/// <summary>
///     Immutable point-in-time copy of a <see cref="BucketHistogram" />. Counts are
///     per-bucket (non-cumulative); the last slot is the +Inf overflow bucket.
///     <see cref="Percentile" /> interpolates linearly within the containing bucket,
///     which is exact enough for dashboard display; Prometheus consumers get the raw
///     buckets and run histogram_quantile themselves.
/// </summary>
public readonly record struct HistogramSnapshot
{
    public long[] UpperBounds { get; init; }

    public long[] Counts { get; init; }

    public long Count { get; init; }

    public long Sum { get; init; }

    public double Percentile(double p)
    {
        if (Count == 0 || Counts == null || UpperBounds == null)
            return 0;

        long rank = Math.Max(1, (long)Math.Ceiling(p * Count));
        long cumulative = 0;

        for (var i = 0; i < Counts.Length; i++)
        {
            cumulative += Counts[i];

            if (cumulative < rank)
                continue;

            // Overflow bucket has no finite upper bound to interpolate to — report the
            // last finite bound (a floor, honest for display purposes).
            if (i == UpperBounds.Length)
                return UpperBounds.Length > 0 ? UpperBounds[^1] : 0;

            double lower = i == 0 ? 0 : UpperBounds[i - 1];
            long intoBucket = rank - (cumulative - Counts[i]);
            return lower + ((UpperBounds[i] - lower) * intoBucket / (double)Counts[i]);
        }

        return UpperBounds.Length > 0 ? UpperBounds[^1] : 0;
    }
}
