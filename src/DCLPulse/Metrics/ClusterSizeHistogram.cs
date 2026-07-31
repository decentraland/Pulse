namespace Pulse.Metrics;

/// <summary>
///     Bucket layout for the cluster-size histogram, shared by the collector that accumulates
///     observations and the formatter that labels them — a boundary list that disagreed between the two
///     would mislabel every bucket.
///     <para />
///     Bounds are exponential over the reachable range (a cluster cannot exceed
///     <c>Transport.MaxPeers</c>): fine where nearly every cluster lands, coarse at the top where only a
///     collapsed partition reaches. Quantiles are therefore approximate to the width of the containing
///     bucket, which is the trade for being able to compute them over any time window instead of
///     reading one pass's pre-computed value.
/// </summary>
internal static class ClusterSizeHistogram
{
    /// <summary>
    ///     Inclusive upper bounds, ascending. Prometheus reports these as <c>le</c> labels.
    /// </summary>
    public static readonly int[] BOUNDS = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096];

    /// <summary>
    ///     One slot per bound plus the <c>+Inf</c> overflow Prometheus requires.
    /// </summary>
    public static readonly int BUCKET_COUNT = BOUNDS.Length + 1;

    /// <summary>
    ///     The slot an observation falls in: the first bound it does not exceed, or the overflow slot.
    ///     Linear over thirteen bounds — cheaper than a binary search at this width, and this runs once
    ///     per cluster per pass rather than on any hot path.
    /// </summary>
    public static int IndexOf(int value)
    {
        for (var i = 0; i < BOUNDS.Length; i++)
            if (value <= BOUNDS[i])
                return i;

        return BOUNDS.Length;
    }
}
