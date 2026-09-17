using Pulse.Presence;
using System.Diagnostics.Metrics;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    /// <summary>
    ///     The <c>engine.parcel_changes</c> feed, recorded once per published batch on the presence loop
    ///     and never on the per-tick or per-packet path. Delivery itself is <see cref="Nats" />'s.
    /// </summary>
    public static class Presence
    {
        /// <summary>Tag key for the <c>reason</c> dimension carried on <see cref="SNAPSHOTS" />.</summary>
        public const string SNAPSHOT_REASON_TAG_KEY = "reason";

        // Cached per-reason tag, indexed by (int)PresenceSnapshotReason, so the dimension attaches to
        // a counter Add() without allocating.
        private static readonly KeyValuePair<string, object?>[] SNAPSHOT_REASON_TAGS = BuildSnapshotReasonTags();

        /// <summary>
        ///     Inclusive upper bounds for <see cref="BATCH_SIZE" />, exponential over the reachable
        ///     range — a batch cannot exceed the peers this server holds. The first bound is 0 because
        ///     a zero-entry batch is a snapshot of an empty server, which would otherwise overflow.
        /// </summary>
        public static readonly long[] BATCH_SIZE_BUCKETS = [0, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096];

        /// <summary>
        ///     Entries in each published batch, snapshots included. The observation count is the number
        ///     of batches, so how often the feed speaks and how much it says come off one instrument.
        /// </summary>
        public static readonly Histogram<int> BATCH_SIZE =
            METER.CreateHistogram<int>("pulse.presence.batch_size");

        /// <summary>
        ///     Full snapshots published, by <see cref="PresenceSnapshotReason" />. Any rate of
        ///     <c>eviction</c> means the outbox is losing changes — raise <c>Nats:ChannelCapacity</c>.
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
