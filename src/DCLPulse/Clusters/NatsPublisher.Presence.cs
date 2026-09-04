using Decentraland.Pulse;
using NATS.Client.Core;
using Pulse.Metrics;
using Pulse.Presence;

namespace Pulse.Clusters;

/// <summary>
///     The <c>engine.parcel_changes</c> half of the publisher (iteration-2 C1). Same outbox shape as
///     the cluster feed and for the same reasons — producers hand over and return, the broker is
///     never on a caller's path — with one difference that follows from the message: a batch is not a
///     message somebody handed in, it is assembled from the outbox when the timer fires, so
///     coalescing is per address and the wire message is built at send time rather than queued.
///     <para />
///     Three states can be pending, and they supersede in one direction only:
///     <list type="bullet">
///         <item>
///             per-address changes, latest-wins per address and capped at
///             <see cref="NatsOptions.ChannelCapacity" /> distinct addresses — the same bound, and
///             the same eviction counter, as an undelivered cluster assignment;
///         </item>
///         <item>
///             a full snapshot, which replaces every pending change because it already states each
///             of those peers' current position, and which a later snapshot replaces in turn;
///         </item>
///         <item>
///             a snapshot <em>request</em>, raised by this class (start, interval, eviction) and
///             answered by <see cref="ParcelChangeTracker" /> on its next pass, because the state a
///             snapshot needs lives there and not here.
///         </item>
///     </list>
///     <para />
///     <c>seq</c> is stamped per assembled batch and never reused. A publish that throws therefore
///     leaves a real gap in the sequence, which is exactly what it is: the batch is gone. Consumers
///     detect the gap, keep serving what they have, and are corrected by the next snapshot — which
///     is why <see cref="PresenceOptions.SnapshotIntervalMs" /> is a recovery deadline rather than a
///     refresh rate.
/// </summary>
public sealed partial class NatsPublisher
{
    // Presence outbox, under the same outboxLock as the cluster feed: every mutation here already
    // sits next to one of those (a pass publishes assignments and presence in the same breath), so
    // sharing the lock keeps one ordering to reason about instead of two.
    private readonly Dictionary<string, PendingParcelChange> pendingParcelChangeByAddress = new (StringComparer.Ordinal);
    private readonly Queue<string> parcelChangeOrder = new ();
    private IReadOnlyList<PeerPresence>? pendingParcelSnapshot;
    private PresenceSnapshotReason pendingParcelSnapshotReason;

    // The batch instance the presence loop owns, reused across batches exactly like the discovery
    // heartbeat's: every field is rewritten before each send, and only after the previous send's
    // task has completed — by which point the client has serialized it.
    private readonly ParcelChangesBatch parcelBatch = new ();
    private readonly Stack<ParcelChange> parcelChangePool = new ();

    // Assembly scratch, reached from TryBuildParcelBatch alone, which the presence loop is the only
    // caller of. Sorting here rather than in the tracker keeps one definition of batch order.
    private readonly List<PendingParcelChange> parcelBatchScratch = [];

    // Requested-but-unanswered snapshot. Null means none outstanding; first-wins, so a request that
    // is already waiting keeps the reason it was raised with rather than being relabelled by a
    // second trigger before the tracker gets to it.
    private PresenceSnapshotReason? parcelSnapshotRequest;

    private long lastParcelSnapshotUnixMs;

    // Monotonic per server_name, from 1, and shared by snapshots and deltas — a snapshot is a batch
    // with a flag set, not a separate stream. Reset by a process restart and nothing else, which is
    // what makes "seq went backwards" mean "that server restarted".
    private ulong parcelSeq;

    /// <summary>
    ///     Whether presence is being published at all: it follows the existing NATS gating, so no
    ///     broker means no feed, and a non-positive batch interval is not a valid timer period.
    ///     Assigned by the constructor, since neither option can change after start.
    /// </summary>
    private readonly bool presenceEnabled;

    /// <summary>
    ///     Queues one peer's presence for the next batch. A change raised while a snapshot is still
    ///     pending is kept, not folded into it: the snapshot states the world as of the pass that
    ///     built it, and anything raised after — an exit, above all — is strictly newer, so it has to
    ///     survive the snapshot and follow it.
    /// </summary>
    public void PublishParcelChange(string address, string realm, ParcelCoord? parcel)
    {
        if (!presenceEnabled) return;

        var change = new PendingParcelChange(address, realm, parcel);
        bool superseded;
        var evicted = false;

        lock (outboxLock)
        {
            if (pendingParcelChangeByAddress.ContainsKey(address))
            {
                superseded = true;
                pendingParcelChangeByAddress[address] = change;
            }
            else
            {
                superseded = false;

                if (pendingParcelChangeByAddress.Count >= options.ChannelCapacity
                    && parcelChangeOrder.TryDequeue(out string? oldest))
                    evicted = pendingParcelChangeByAddress.Remove(oldest);

                pendingParcelChangeByAddress[address] = change;
                parcelChangeOrder.Enqueue(address);
            }
        }

        // Counted after the lock is released, for the same reason as everywhere else here:
        // Counter.Add runs every registered MeterListener callback inline, and the outbox lock is
        // what the publish loops need to make progress.
        if (superseded)
            CountSuperseded();
        else if (evicted)
            CountDropped();
    }

    public void PublishParcelSnapshot(IReadOnlyList<PeerPresence> presence, PresenceSnapshotReason reason)
    {
        if (!presenceEnabled) return;

        int superseded;

        lock (outboxLock)
        {
            // Every pending change names a peer this snapshot also names, with state the snapshot has
            // already superseded — and delivering them after it would move a peer backwards.
            superseded = pendingParcelChangeByAddress.Count;
            pendingParcelChangeByAddress.Clear();
            parcelChangeOrder.Clear();

            if (pendingParcelSnapshot is not null)
                superseded++;

            pendingParcelSnapshot = presence;
            pendingParcelSnapshotReason = reason;
        }

        for (var i = 0; i < superseded; i++)
            CountSuperseded();
    }

    public bool TryTakeParcelSnapshotRequest(out PresenceSnapshotReason reason)
    {
        lock (outboxLock)
        {
            if (parcelSnapshotRequest is not { } requested)
            {
                reason = default(PresenceSnapshotReason);
                return false;
            }

            parcelSnapshotRequest = null;
            reason = requested;

            return true;
        }
    }

    /// <summary>
    ///     Raises a snapshot request, which the tracker answers on its next pass. First-wins: a
    ///     request already waiting keeps its original reason, so the label reports what first made
    ///     the delta stream insufficient rather than whatever fired last.
    /// </summary>
    private void RequestParcelSnapshot(PresenceSnapshotReason reason)
    {
        if (!presenceEnabled) return;

        lock (outboxLock)
            parcelSnapshotRequest ??= reason;
    }

    /// <summary>
    ///     Assembles the next batch from the outbox, or reports that there is nothing to send: an
    ///     empty delta is not published, since "no peer moved" is not news, while a snapshot always is
    ///     — an empty one says this server holds nobody, which a consumer has no other way to learn.
    ///     <para />
    ///     Entries are ordered by address so a batch is a function of its content alone: two servers
    ///     with the same state produce the same bytes, and the wire fixtures can be compared byte for
    ///     byte. <paramref name="snapshotReason" /> is non-null exactly when the batch is a snapshot.
    ///     <para />
    ///     Internal so the batch — and the bytes it serializes to — can be asserted without a broker.
    /// </summary>
    internal bool TryBuildParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? snapshotReason)
    {
        batch = parcelBatch;
        snapshotReason = null;

        IReadOnlyList<PeerPresence>? snapshot;

        parcelBatchScratch.Clear();

        lock (outboxLock)
        {
            snapshot = pendingParcelSnapshot;
            pendingParcelSnapshot = null;

            if (snapshot is not null)
                snapshotReason = pendingParcelSnapshotReason;
            else
            {
                if (pendingParcelChangeByAddress.Count == 0) return false;

                foreach (KeyValuePair<string, PendingParcelChange> pending in pendingParcelChangeByAddress)
                    parcelBatchScratch.Add(pending.Value);

                pendingParcelChangeByAddress.Clear();
                parcelChangeOrder.Clear();
            }
        }

        if (snapshot is not null)
            foreach (PeerPresence presence in snapshot)
                parcelBatchScratch.Add(new PendingParcelChange(presence.Address, presence.Realm, presence.Parcel));

        parcelBatchScratch.Sort(static (a, b) => string.CompareOrdinal(a.Address, b.Address));

        FillParcelBatch(snapshotReason is not null);

        return true;
    }

    /// <summary>
    ///     Projects <see cref="parcelBatchScratch" /> onto the reused batch instance. Every field of
    ///     the batch and of each entry in use is rewritten, and the entries the previous batch used
    ///     go back to the free list before the list is emptied, so nothing survives the batch this
    ///     instance last held.
    /// </summary>
    private void FillParcelBatch(bool snapshot)
    {
        for (var i = 0; i < parcelBatch.Changes.Count; i++)
            parcelChangePool.Push(parcelBatch.Changes[i]);

        // RepeatedField.Clear keeps its backing array, so refilling the list allocates nothing.
        parcelBatch.Changes.Clear();

        parcelBatch.ServerName = options.ServerName;
        parcelBatch.Seq = ++parcelSeq;
        parcelBatch.Snapshot = snapshot;
        parcelBatch.ServerTime = (ulong)timeProvider.UnixTimeMs;

        foreach (PendingParcelChange pending in parcelBatchScratch)
        {
            ParcelChange change = RentParcelChange();

            change.Address = pending.Address;
            change.Realm = pending.Realm;

            if (pending.Parcel is { } parcel)
            {
                // The entry's own parcel when it still has one, so a steady stream of placements
                // allocates nothing.
                Parcel target = change.Parcel ?? new Parcel();

                target.X = parcel.X;
                target.Y = parcel.Y;
                change.Parcel = target;
            }
            else

                // An absent parcel is the exit signal, so the reference is dropped rather than
                // zeroed: a (0,0) parcel is the world origin, a placement like any other.
                change.Parcel = null;

            parcelBatch.Changes.Add(change);
        }
    }

    private ParcelChange RentParcelChange() =>
        parcelChangePool.TryPop(out ParcelChange? pooled) ? pooled : new ParcelChange();

    /// <summary>
    ///     Publishes a batch once per <see cref="PresenceOptions.BatchIntervalMs" />, and raises the
    ///     interval snapshot request. Sent straight to the connection rather than through the
    ///     wake-signalled drain: a batch does not exist until the timer fires, so there is nothing to
    ///     queue. A fault that ends the loop is logged once and cancels <paramref name="loops" />.
    /// </summary>
    private async Task PublishParcelChangesPeriodicallyAsync(NatsConnection connection, CancellationTokenSource loops)
    {
        CancellationToken token = loops.Token;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(presenceOptions.BatchIntervalMs));

            while (!token.IsCancellationRequested)
            {
                RequestParcelSnapshotIfDue();

                if (TryBuildParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? snapshotReason))
                {
                    PulseMetrics.Presence.BATCH_SIZE.Record(batch.Changes.Count);

                    if (snapshotReason is { } reason)
                    {
                        lastParcelSnapshotUnixMs = timeProvider.UnixTimeMs;
                        PulseMetrics.Presence.SNAPSHOTS.Add(1, PulseMetrics.Presence.ReasonTag(reason));
                    }

                    try
                    {
                        await connection.PublishAsync(
                            PARCEL_CHANGES_SUBJECT, batch, serializer: SERIALIZER, cancellationToken: token);

                        CountPublished();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        CountPublishFailed();

                        logger.LogWarning(e,
                            "Failed to publish presence batch {Seq}; consumers will see the gap and wait for the next snapshot",
                            batch.Seq);
                    }
                }

                try { await timer.WaitForNextTickAsync(token); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown, or a sibling loop faulted and cancelled this one.
        }
        catch (Exception e)
        {
            logger.LogError(e, "Presence publisher stopped unexpectedly; engine.parcel_changes is no longer being delivered");
            loops.Cancel();
        }
    }

    /// <summary>
    ///     Raises the periodic snapshot request. Measured from the last snapshot <em>published</em>,
    ///     not requested, so a request the tracker has not answered yet — a stopped clustering pass,
    ///     say — does not silently reset the deadline it exists to enforce.
    /// </summary>
    private void RequestParcelSnapshotIfDue()
    {
        if (presenceOptions.SnapshotIntervalMs <= 0) return;

        if (timeProvider.UnixTimeMs - lastParcelSnapshotUnixMs >= presenceOptions.SnapshotIntervalMs)
            RequestParcelSnapshot(PresenceSnapshotReason.Interval);
    }

    /// <summary>
    ///     One peer's undelivered presence: the values a <see cref="ParcelChange" /> is built from,
    ///     held as a value rather than a pooled message because the wire message does not exist until
    ///     the batch is assembled.
    /// </summary>
    private readonly record struct PendingParcelChange(string Address, string Realm, ParcelCoord? Parcel);
}
