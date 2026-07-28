using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    /// <summary>
    ///     Clustering pass instruments. Recorded once per pass on the tracker thread, never on the
    ///     per-tick or per-packet path.
    /// </summary>
    public static class Clusters
    {
        /// <summary>
        ///     Clusters currently derived. Recorded as a delta against the previous pass so the
        ///     collector can accumulate it like any other up-down counter.
        /// </summary>
        public static readonly UpDownCounter<int> COUNT =
            METER.CreateUpDownCounter<int>("pulse.clusters.count");

        public static readonly Counter<long> PASSES =
            METER.CreateCounter<long>("pulse.clusters.passes");

        /// <summary>
        ///     Cumulative pass wall time in microseconds. Divide by <see cref="PASSES" /> for the
        ///     mean — the sum/count pair Prometheus expects rather than a pre-averaged gauge.
        /// </summary>
        public static readonly Counter<long> PASS_DURATION_US =
            METER.CreateCounter<long>("pulse.clusters.pass_duration_us");

        /// <summary>
        ///     Published assignment changes. Counts post-debounce transitions only, so it measures
        ///     churn a consumer actually sees rather than raw pass-to-pass jitter.
        /// </summary>
        public static readonly Counter<long> REASSIGNMENTS =
            METER.CreateCounter<long>("pulse.clusters.reassignments");
    }

    /// <summary>
    ///     Broker instruments for the outbound cluster feed.
    /// </summary>
    public static class Nats
    {
        public static readonly Counter<long> PUBLISHED =
            METER.CreateCounter<long>("pulse.nats.published");

        /// <summary>
        ///     Messages genuinely lost — evicted because too many distinct peers were pending at
        ///     once, or failed at the broker. Non-zero means some peer may be addressed by a stale
        ///     cluster, so this is the actionable signal, not <see cref="SUPERSEDED" />.
        /// </summary>
        public static readonly Counter<long> DROPPED =
            METER.CreateCounter<long>("pulse.nats.dropped");

        /// <summary>
        ///     Messages replaced before delivery by a newer one for the same subject. Expected under
        ///     load and harmless: the replacement carries strictly fresher state.
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
