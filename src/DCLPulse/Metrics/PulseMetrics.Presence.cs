using Pulse.Presence;
using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    /// <summary>
    ///     The <c>engine.parcel_changes</c> feed, recorded once per published batch on the presence
    ///     loop and never on the per-tick or per-packet path. Delivery itself is counted by
    ///     <see cref="Nats" />, which these two do not duplicate: they answer what a batch contained
    ///     and why it was a snapshot.
    /// </summary>
    public static class Presence
    {
        /// <summary>Tag key for the <c>reason</c> dimension carried on <see cref="SNAPSHOTS" />.</summary>
        public const string SNAPSHOT_REASON_TAG_KEY = "reason";

        // Cached per-reason tag, indexed by (int)PresenceSnapshotReason, so the dimension attaches to
        // a counter Add() without allocating. The boxed enum value is unboxed by
        // MeterListenerMetricsCollector to bucket the measurement.
        private static readonly KeyValuePair<string, object?>[] SNAPSHOT_REASON_TAGS = BuildSnapshotReasonTags();

        /// <summary>
        ///     Inclusive upper bounds for <see cref="BATCH_SIZE" />. Exponential over the reachable
        ///     range — a batch cannot exceed the peers this server holds — so resolution is fine
        ///     where an ordinary delta lands and coarse where only a snapshot of a full server does.
        ///     A zero-entry batch is a snapshot of an empty server, so the first bucket has to admit
        ///     it: a count of zero falls below every bound and lands in the overflow bucket
        ///     otherwise.
        /// </summary>
        public static readonly long[] BATCH_SIZE_BUCKETS = [0, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096];

        /// <summary>
        ///     Entries in each published batch, snapshots included. The count of observations is the
        ///     number of batches, so the two questions an operator asks of this feed — how often it
        ///     speaks and how much it says — come off one instrument.
        /// </summary>
        public static readonly Histogram<int> BATCH_SIZE =
            METER.CreateHistogram<int>("pulse.presence.batch_size");

        /// <summary>
        ///     Full snapshots published, by <see cref="PresenceSnapshotReason" />. A steady trickle
        ///     of <c>interval</c> is the healthy shape; any rate of <c>eviction</c> means the outbox
        ///     is losing changes and the same lever applies as for
        ///     <c>dcl_pulse_nats_dropped_total</c> — raise <c>Nats:ChannelCapacity</c>.
        /// </summary>
        public static readonly Counter<long> SNAPSHOTS =
            METER.CreateCounter<long>("pulse.presence.snapshots");

        /// <summary>The pre-built tag for one snapshot reason.</summary>
        public static KeyValuePair<string, object?> ReasonTag(PresenceSnapshotReason reason) =>
            SNAPSHOT_REASON_TAGS[(int)reason];

        private static KeyValuePair<string, object?>[] BuildSnapshotReasonTags()
        {
            PresenceSnapshotReason[] reasons = Enum.GetValues<PresenceSnapshotReason>();
            var tags = new KeyValuePair<string, object?>[reasons.Length];

            foreach (PresenceSnapshotReason reason in reasons)
                tags[(int)reason] = new KeyValuePair<string, object?>(SNAPSHOT_REASON_TAG_KEY, reason);

            return tags;
        }
    }
}
