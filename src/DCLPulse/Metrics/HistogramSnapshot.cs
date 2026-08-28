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
    public long[]? UpperBounds { get; init; }

    public long[]? Counts { get; init; }

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

/// <summary>
///     Display-side helpers. Merge sums per-bucket counts across snapshots that share the
///     same bucket bounds (e.g. the 7 per-continent RTT histograms → one aggregate row on
///     the console dashboard). Parts that are default (null arrays) are skipped.
/// </summary>
public static class HistogramSnapshots
{
    public static HistogramSnapshot Merge(HistogramSnapshot[]? parts)
    {
        if (parts == null)
            return default(HistogramSnapshot);

        long[]? counts = null;
        long[]? bounds = null;
        long total = 0, sum = 0;

        foreach (HistogramSnapshot part in parts)
        {
            if (part.Counts == null)
                continue;

            bounds ??= part.UpperBounds;

            // All real callers share one bounds array (the 7 per-continent RTT histograms are
            // built from PulseMetrics.Transport.RTT_BUCKETS_MS). Mismatched bounds would sum
            // counts across incompatible bucket edges and silently skew merged percentiles, so
            // guard unconditionally — a Debug.Assert would compile out of Release builds.
            if (!ReferenceEquals(bounds, part.UpperBounds))
                throw new ArgumentException(
                    "HistogramSnapshots.Merge requires all parts to share one bounds array.",
                    nameof(parts));

            counts ??= new long[part.Counts.Length];

            for (var i = 0; i < part.Counts.Length && i < counts.Length; i++)
                counts[i] += part.Counts[i];

            total += part.Count;
            sum += part.Sum;
        }

        return counts == null
            ? default(HistogramSnapshot)
            : new HistogramSnapshot { UpperBounds = bounds, Counts = counts, Count = total, Sum = sum };
    }
}
