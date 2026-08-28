using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    /// <summary>
    ///     Clustering pass instruments, recorded once per pass on the tracker thread and never on the
    ///     per-tick or per-packet path.
    /// </summary>
    public static class Clusters
    {
        /// <summary>
        ///     Clusters currently derived, recorded as a delta against the previous pass.
        /// </summary>
        public static readonly UpDownCounter<int> COUNT =
            METER.CreateUpDownCounter<int>("pulse.clusters.count");

        public static readonly Counter<long> PASSES =
            METER.CreateCounter<long>("pulse.clusters.passes");

        /// <summary>
        ///     Cumulative pass wall time in microseconds. Divide by <see cref="PASSES" /> for the mean;
        ///     a sum/count pair rather than a pre-averaged gauge.
        /// </summary>
        public static readonly Counter<long> PASS_DURATION_US =
            METER.CreateCounter<long>("pulse.clusters.pass_duration_us");

        /// <summary>
        ///     Published assignment changes: post-debounce transitions only, not raw pass-to-pass
        ///     jitter.
        /// </summary>
        public static readonly Counter<long> REASSIGNMENTS =
            METER.CreateCounter<long>("pulse.clusters.reassignments");

        /// <summary>
        ///     Peers placed in a cluster this pass, recorded as a delta against the previous pass.
        ///     Paired with <see cref="COUNT" /> rather than pre-averaged, so the mean cluster size is
        ///     <c>peers / count</c> and stays aggregatable across instances.
        /// </summary>
        public static readonly UpDownCounter<int> PEERS =
            METER.CreateUpDownCounter<int>("pulse.clusters.peers");

        /// <summary>
        ///     Inclusive upper bounds for <see cref="SIZE" />. Exponential over the reachable range —
        ///     a cluster cannot exceed <c>Transport.MaxPeers</c> — so resolution is fine where nearly
        ///     every cluster lands and coarse at the top, where only a collapsed partition reaches.
        /// </summary>
        public static readonly long[] SIZE_BUCKETS = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096];

        /// <summary>
        ///     One observation per cluster per pass, bucketed by <see cref="SIZE_BUCKETS" /> so
        ///     quantiles are computed at query time. A pre-computed quantile could not be aggregated —
        ///     the mean of medians is not the median of the union, and no window but the pass itself
        ///     could be asked about.
        /// </summary>
        public static readonly Histogram<int> SIZE =
            METER.CreateHistogram<int>("pulse.clusters.size");

        /// <summary>
        ///     The largest cluster of this pass, as a delta against the previous one like
        ///     <see cref="COUNT" />. Kept as its own gauge because <see cref="SIZE" /> cannot recover it:
        ///     the top bucket only reports that something exceeded its bound.
        /// </summary>
        public static readonly UpDownCounter<int> SIZE_MAX =
            METER.CreateUpDownCounter<int>("pulse.clusters.size_max");
    }

    /// <summary>
    ///     Broker instruments for the outbound cluster feed.
    /// </summary>
    public static class Nats
    {
        public static readonly Counter<long> PUBLISHED =
            METER.CreateCounter<long>("pulse.nats.published");

        /// <summary>
        ///     Publishes that threw, counted for the outbox drain and the discovery heartbeat alike.
        ///     Every one of them is raised client-side — a timeout, a connect failure, an oversized
        ///     payload, a subject the client rejects — because core NATS never acknowledges a PUB, so a
        ///     broker refusing one cannot fail the call. The lever is the broker or the path to it,
        ///     never <c>Nats:ChannelCapacity</c>.
        /// </summary>
        public static readonly Counter<long> PUBLISH_FAILED =
            METER.CreateCounter<long>("pulse.nats.publish_failed");

        /// <summary>
        ///     Messages genuinely lost to eviction — more than <c>Nats:ChannelCapacity</c> distinct
        ///     peers held an undelivered assignment at once, so the longest-admitted one was pushed out.
        ///     The actionable signal for capacity, unlike <see cref="SUPERSEDED" />; a publish that
        ///     failed is counted by <see cref="PUBLISH_FAILED" /> instead.
        /// </summary>
        public static readonly Counter<long> DROPPED =
            METER.CreateCounter<long>("pulse.nats.dropped");

        /// <summary>
        ///     Messages replaced before delivery by a newer one for the same subject. Harmless: the
        ///     replacement carries strictly fresher state.
        /// </summary>
        public static readonly Counter<long> SUPERSEDED =
            METER.CreateCounter<long>("pulse.nats.superseded");

        public static readonly Counter<long> RECONNECTS =
            METER.CreateCounter<long>("pulse.nats.reconnects");

        /// <summary>
        ///     1 while the broker connection is up, 0 otherwise. Stays 0 in stats-only mode.
        /// </summary>
        public static readonly UpDownCounter<int> CONNECTED =
            METER.CreateUpDownCounter<int>("pulse.nats.connected");
    }
}
