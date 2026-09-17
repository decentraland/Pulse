using Decentraland.Pulse;
using NATS.Client.Core;
using Pulse.Metrics;

namespace Pulse.Clusters;

/// <summary>
///     The <c>engine.parcel_changes</c> half of the publisher (iteration-2 C1). Same fail-soft outbox
///     as the cluster feed, with one difference that follows from the message: a batch is assembled
///     from the outbox when the timer fires rather than handed in, so coalescing is per address and
///     the wire message is built at send time.
///     <para />
///     Three things can be pending: per-address changes, latest-wins and capped at
///     <see cref="NatsOptions.ChannelCapacity" /> addresses; a full snapshot, which never discards a
///     pending change (A2); and a snapshot <em>request</em>, answered by
///     <see cref="ClusterTracker" />, which holds the state a snapshot needs.
/// </summary>
public sealed partial class NatsPublisher
{
    /// <summary>
    ///     What <see cref="PresenceOptions.SnapshotIntervalMs" /> is divided by to bound eviction
    ///     snapshots: at most one per quarter of the recovery deadline, 15 s on the defaults. An
    ///     eviction means the broker is already behind, so one full-population snapshot per evicting
    ///     batch would push this feed's largest message exactly when it is dropping messages.
    /// </summary>
    internal const int EVICTION_SNAPSHOT_INTERVAL_DIVISOR = 4;

    /// <summary>
    ///     Batches one turn of the presence cadence may publish. Two, because a snapshot goes out
    ///     behind the delta batch that was pending when it was collected (A2); no more, so a producer
    ///     that keeps enqueueing cannot hold the turn and turn the cadence into a busy loop.
    /// </summary>
    internal const int MAX_PARCEL_BATCHES_PER_TURN = 2;

    // Presence outbox, under the same outboxLock as the cluster feed — one ordering, not two.
    private readonly Dictionary<string, PendingParcelChange> pendingParcelChangeByAddress = new (StringComparer.Ordinal);
    private readonly Queue<string> parcelChangeOrder = new ();
    private IReadOnlyList<PeerPresence>? pendingParcelSnapshot;
    private PresenceSnapshotReason pendingParcelSnapshotReason;

    // Changes that were already pending when a snapshot was collected. Older than it, so they go out
    // ahead of it under their own seq (A2). Latest-wins per address and deliberately uncapped — the
    // bound is the addresses this server holds, and dropping one is the loss this staging prevents.
    private readonly Dictionary<string, PendingParcelChange> parcelChangesAheadOfSnapshot = new (StringComparer.Ordinal);

    // The batch instance the presence loop owns, reused across sends; every field is rewritten first.
    private readonly ParcelChangesBatch parcelBatch = new ();
    private readonly Stack<ParcelChange> parcelChangePool = new ();

    // Assembly scratch for TryBuildParcelBatch, its only caller; sorting here keeps one batch order.
    private readonly List<PendingParcelChange> parcelBatchScratch = [];

    // Requested-but-unanswered snapshot; null means none, and first-wins keeps the original reason.
    private PresenceSnapshotReason? parcelSnapshotRequest;

    private long lastParcelSnapshotUnixMs;

    // Last eviction-snapshot request (zero = none yet, so the first eviction is answered at once).
    private long lastEvictionSnapshotRequestUnixMs;

    // Monotonic per server_name, from 1, shared by snapshots and deltas. Never reused, so a publish
    // that throws leaves a real gap, which the next snapshot repairs. Reset only by a restart.
    private ulong parcelSeq;

    // The NATS gating plus a batch interval that is a valid timer period. Assigned by the
    // constructor — neither option can change after start.
    private readonly bool presenceEnabled;

    /// <summary>
    ///     Queues one peer's presence for the next batch. A change raised while a snapshot is pending
    ///     survives it and follows it: it is strictly newer than the pass that snapshot states.
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

        // Counted outside the lock, for the reason QueueChange gives.
        if (superseded)
            CountSuperseded();
        else if (evicted)
            CountDropped();
    }

    public void PublishParcelSnapshot(IReadOnlyList<PeerPresence> presence, PresenceSnapshotReason reason)
    {
        if (!presenceEnabled) return;

        var superseded = 0;

        lock (outboxLock)
        {
            // Staged, not cleared (A2): older than the snapshot, so they go out ahead of it under
            // their own seq. Dropping them loses every exit a snapshot cannot name.
            foreach (KeyValuePair<string, PendingParcelChange> pending in pendingParcelChangeByAddress)
                parcelChangesAheadOfSnapshot[pending.Key] = pending.Value;

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
    ///     Raises a snapshot request, answered by the tracker on its next pass. First-wins, so a
    ///     request already waiting keeps its original reason. Eviction requests are additionally
    ///     coalesced to one per <see cref="PresenceOptions.SnapshotIntervalMs" /> /
    ///     <see cref="EVICTION_SNAPSHOT_INTERVAL_DIVISOR" />; a non-positive interval leaves them
    ///     unbounded, the recovery deadline having been turned off.
    /// </summary>
    private void RequestParcelSnapshot(PresenceSnapshotReason reason)
    {
        if (!presenceEnabled) return;

        lock (outboxLock)
        {
            if (parcelSnapshotRequest is not null) return;

            if (reason == PresenceSnapshotReason.Eviction)
            {
                long now = timeProvider.UnixTimeMs;
                int window = Math.Max(0, presenceOptions.SnapshotIntervalMs) / EVICTION_SNAPSHOT_INTERVAL_DIVISOR;

                if (now - lastEvictionSnapshotRequestUnixMs < window) return;

                lastEvictionSnapshotRequestUnixMs = now;
            }

            parcelSnapshotRequest = reason;
        }
    }

    /// <summary>
    ///     Assembles the next batch from the outbox, or reports there is nothing to send: an empty
    ///     delta is not published, while an empty snapshot is — it says this server holds nobody.
    ///     <paramref name="snapshotReason" /> is non-null exactly when the batch is a snapshot.
    ///     Internal so the batch, and the bytes it serializes to, can be asserted without a broker.
    ///     <para />
    ///     Entries are ordered by address, so a batch is a function of its content alone. What is
    ///     waiting goes out in the order it happened — changes pending when a snapshot was collected,
    ///     then that snapshot, then anything raised after it — which makes it lossless both ways (A2).
    /// </summary>
    internal bool TryBuildParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? snapshotReason)
    {
        batch = parcelBatch;
        snapshotReason = null;

        IReadOnlyList<PeerPresence>? snapshot;

        parcelBatchScratch.Clear();

        lock (outboxLock)
        {
            snapshot = null;

            if (parcelChangesAheadOfSnapshot.Count > 0)
            {
                foreach (KeyValuePair<string, PendingParcelChange> pending in parcelChangesAheadOfSnapshot)
                    parcelBatchScratch.Add(pending.Value);

                parcelChangesAheadOfSnapshot.Clear();
            }
            else if (pendingParcelSnapshot is { } collected)
            {
                snapshot = collected;
                snapshotReason = pendingParcelSnapshotReason;
                pendingParcelSnapshot = null;
            }
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

        // A total order only because every source is keyed by address, the snapshot list included
        // (C1.3): two entries for one address would compare equal, and List<T>.Sort is not stable.
        parcelBatchScratch.Sort(static (a, b) => string.CompareOrdinal(a.Address, b.Address));

        FillParcelBatch(snapshotReason is not null);

        return true;
    }

    /// <summary>
    ///     Projects <see cref="parcelBatchScratch" /> onto the reused batch instance: every field in
    ///     use is rewritten and the previous batch's entries go back to the free list first, so
    ///     nothing survives the batch this instance last held.
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
                // Reuses the entry's own parcel, so a steady stream of placements allocates nothing.
                Parcel target = change.Parcel ?? new Parcel();

                target.X = parcel.X;
                target.Y = parcel.Y;
                change.Parcel = target;
            }
            else

                // An absent parcel is the exit signal, so the reference is dropped rather than
                // zeroed: (0,0) is the world origin, a placement like any other.
                change.Parcel = null;

            parcelBatch.Changes.Add(change);
        }
    }

    private ParcelChange RentParcelChange() =>
        parcelChangePool.TryPop(out ParcelChange? pooled) ? pooled : new ParcelChange();

    /// <summary>
    ///     One turn of the presence cadence: raise the interval request if due, take what the outbox
    ///     holds, and account for it — batch size, and for a snapshot the reason counter and the
    ///     deadline it resets. Everything a batch does but the publish, so a test drives the cadence
    ///     the loop runs. False means nothing to send this interval.
    /// </summary>
    internal bool TryTakeNextParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? snapshotReason)
    {
        RequestParcelSnapshotIfDue();

        if (!TryBuildParcelBatch(out batch, out snapshotReason))
            return false;

        PulseMetrics.Presence.BATCH_SIZE.Record(batch.Changes.Count);

        if (snapshotReason is { } reason)
        {
            // Stamped from the batch that carries the snapshot, not from the request that asked for
            // one, so the deadline measures what was actually sent.
            lastParcelSnapshotUnixMs = timeProvider.UnixTimeMs;
            PulseMetrics.Presence.SNAPSHOTS.Add(1, PulseMetrics.Presence.ReasonTag(reason));
        }

        return true;
    }

    /// <summary>
    ///     Publishes up to <see cref="MAX_PARCEL_BATCHES_PER_TURN" /> batches once per
    ///     <see cref="PresenceOptions.BatchIntervalMs" />, straight to the connection rather than
    ///     through the wake-signalled drain: a batch does not exist until the timer fires, so there is
    ///     nothing to queue. Awaiting each publish before assembling the next keeps the reused batch
    ///     instance safe. A fault that ends the loop is logged once and cancels <paramref name="loops" />.
    /// </summary>
    private async Task PublishParcelChangesPeriodicallyAsync(NatsConnection connection, CancellationTokenSource loops)
    {
        CancellationToken token = loops.Token;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(presenceOptions.BatchIntervalMs));

            while (!token.IsCancellationRequested)
            {
                for (var sent = 0; sent < MAX_PARCEL_BATCHES_PER_TURN; sent++)
                {
                    if (!TryTakeNextParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? _))
                        break;

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
    ///     not requested, so a request the tracker has not answered — a stopped clustering pass, say
    ///     — does not reset the deadline it exists to enforce. A snapshot already in flight does
    ///     satisfy it: the request is answered a pass later and published the turn after, so a second
    ///     one in that window costs a full snapshot saying what the first said. Checked under the
    ///     outbox lock together with the set, so a snapshot being taken elsewhere cannot slip between.
    /// </summary>
    private void RequestParcelSnapshotIfDue()
    {
        if (!presenceEnabled) return;
        if (presenceOptions.SnapshotIntervalMs <= 0) return;
        if (timeProvider.UnixTimeMs - lastParcelSnapshotUnixMs < presenceOptions.SnapshotIntervalMs) return;

        lock (outboxLock)
        {
            if (pendingParcelSnapshot is not null) return;

            parcelSnapshotRequest ??= PresenceSnapshotReason.Interval;
        }
    }

    /// <summary>
    ///     One peer's undelivered presence: the values a <see cref="ParcelChange" /> is built from,
    ///     held as a value because the wire message does not exist until the batch is assembled.
    /// </summary>
    private readonly record struct PendingParcelChange(string Address, string Realm, ParcelCoord? Parcel);
}
