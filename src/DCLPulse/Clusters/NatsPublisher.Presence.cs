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
///             a full snapshot, which a later snapshot replaces in turn — but which never discards a
///             pending change (A2): the batch that was pending when it was collected goes out ahead
///             of it under its own <c>seq</c>, because the snapshot supersedes the placements among
///             those changes but cannot name an exit, whose slot is already gone;
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
    /// <summary>
    ///     What <see cref="PresenceOptions.SnapshotIntervalMs" /> is divided by to bound eviction
    ///     snapshots: at most one per quarter of the recovery deadline, 15 s on the defaults.
    ///     <para />
    ///     An eviction means the broker is already behind, and a full-population snapshot is the
    ///     largest message this feed can produce — so raising one per batch for as long as the outbox
    ///     keeps evicting (a slow broker with more than <see cref="NatsOptions.ChannelCapacity" />
    ///     peers reassigning) would push the biggest message it has at exactly the moment it is
    ///     already dropping messages. The first eviction is still answered immediately, which is the
    ///     half that repairs the loss; the ones inside the window are already covered by it.
    /// </summary>
    internal const int EVICTION_SNAPSHOT_INTERVAL_DIVISOR = 4;

    /// <summary>
    ///     Batches one turn of the presence cadence may publish. Two, because a snapshot is published
    ///     behind the delta batch that was pending when it was collected (A2) — never more, so a
    ///     producer that keeps enqueueing cannot hold the turn and turn the cadence into a busy loop.
    ///     Anything raised after the snapshot was collected is newer than it and goes out on the next
    ///     turn, in order, which is the cadence C1.4 specifies.
    /// </summary>
    internal const int MAX_PARCEL_BATCHES_PER_TURN = 2;

    // Presence outbox, under the same outboxLock as the cluster feed: every mutation here already
    // sits next to one of those (a pass publishes assignments and presence in the same breath), so
    // sharing the lock keeps one ordering to reason about instead of two.
    private readonly Dictionary<string, PendingParcelChange> pendingParcelChangeByAddress = new (StringComparer.Ordinal);
    private readonly Queue<string> parcelChangeOrder = new ();
    private IReadOnlyList<PeerPresence>? pendingParcelSnapshot;
    private PresenceSnapshotReason pendingParcelSnapshotReason;

    // Changes that were already pending when a snapshot was collected. They are older than the
    // snapshot, so they go out ahead of it under their own seq (A2) rather than being cleared: the
    // snapshot supersedes the placements among them — it states those peers' positions itself — but
    // it cannot name an exit, because OnPeerRemoved cleared that slot and CollectLivePresence no
    // longer lists the peer. Clearing them made that exit path publish nothing at all.
    //
    // Latest-wins per address, and deliberately not capped at ChannelCapacity: the bound here is the
    // number of addresses this server holds, and dropping an entry is the loss this staging exists
    // to prevent.
    private readonly Dictionary<string, PendingParcelChange> parcelChangesAheadOfSnapshot = new (StringComparer.Ordinal);

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

    // When an eviction snapshot was last requested, which is what
    // EVICTION_SNAPSHOT_INTERVAL_DIVISOR bounds. Zero means none yet, so the first eviction of the
    // process is always answered at once.
    private long lastEvictionSnapshotRequestUnixMs;

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

        var superseded = 0;

        lock (outboxLock)
        {
            // Staged rather than cleared (A2): these changes are older than the snapshot, so they are
            // published ahead of it under their own seq. Delivering them *after* it would move a peer
            // backwards, and dropping them loses every exit the snapshot cannot name.
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
    ///     Raises a snapshot request, which the tracker answers on its next pass. First-wins: a
    ///     request already waiting keeps its original reason, so the label reports what first made
    ///     the delta stream insufficient rather than whatever fired last — and an eviction that
    ///     happens while any request is outstanding is repaired by that snapshot anyway.
    ///     <para />
    ///     Eviction requests are additionally coalesced to one per
    ///     <see cref="PresenceOptions.SnapshotIntervalMs" /> /
    ///     <see cref="EVICTION_SNAPSHOT_INTERVAL_DIVISOR" />, measured from the last one raised. A
    ///     non-positive snapshot interval leaves them unbounded, which is the reading that follows
    ///     from the operator having turned the recovery deadline off.
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
    ///     Assembles the next batch from the outbox, or reports that there is nothing to send: an
    ///     empty delta is not published, since "no peer moved" is not news, while a snapshot always is
    ///     — an empty one says this server holds nobody, which a consumer has no other way to learn.
    ///     <para />
    ///     Entries are ordered by address so a batch is a function of its content alone: two servers
    ///     with the same state produce the same bytes, and the wire fixtures can be compared byte for
    ///     byte. <paramref name="snapshotReason" /> is non-null exactly when the batch is a snapshot.
    ///     <para />
    ///     Three things can be waiting, and they go out in the order they happened: the changes that
    ///     were pending when a snapshot was collected, then that snapshot, then anything raised after
    ///     it. That is what makes a snapshot lossless in both directions (A2) — an exit it cannot name
    ///     still reaches consumers, and a move newer than it is not overwritten by it.
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

        // A total order, and only because every source above is keyed by address: the two pending
        // maps by construction, and the snapshot list by ParcelChangeTracker.CollectLivePresence,
        // which reduces a wallet briefly standing on two slots to one entry (C1.3). Two entries for
        // one address would compare equal, and List<T>.Sort is not stable — so the emitted order
        // would stop being a function of the batch's content and the stale entry could land last.
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
    ///     One turn of the presence cadence: raise the interval snapshot request if it is due, take
    ///     whatever the outbox now holds, and account for it — the batch-size observation, and for a
    ///     snapshot the reason counter and the deadline this batch resets. Everything that happens per
    ///     batch other than the publish itself, so the cadence a test drives is the cadence the loop
    ///     runs.
    ///     <para />
    ///     False means there is nothing to send this interval.
    /// </summary>
    internal bool TryTakeNextParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? snapshotReason)
    {
        RequestParcelSnapshotIfDue();

        if (!TryBuildParcelBatch(out batch, out snapshotReason))
            return false;

        PulseMetrics.Presence.BATCH_SIZE.Record(batch.Changes.Count);

        if (snapshotReason is { } reason)
        {
            // Stamped from the batch that carries the snapshot rather than from the request that
            // asked for one, so the deadline measures what consumers actually received.
            lastParcelSnapshotUnixMs = timeProvider.UnixTimeMs;
            PulseMetrics.Presence.SNAPSHOTS.Add(1, PulseMetrics.Presence.ReasonTag(reason));
        }

        return true;
    }

    /// <summary>
    ///     Publishes up to <see cref="MAX_PARCEL_BATCHES_PER_TURN" /> batches once per
    ///     <see cref="PresenceOptions.BatchIntervalMs" />. Sent straight to the connection rather than
    ///     through the wake-signalled drain: a batch does not exist until the timer fires, so there is
    ///     nothing to queue. A fault that ends the loop is logged once and cancels
    ///     <paramref name="loops" />.
    ///     <para />
    ///     More than one per turn only when a snapshot is travelling behind the batch that was pending
    ///     when it was collected (A2). Awaiting each publish before assembling the next is what keeps
    ///     the reused batch instance safe — the client has serialized it by the time its task
    ///     completes.
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
    ///     not requested, so a request the tracker has not answered yet — a stopped clustering pass,
    ///     say — does not silently reset the deadline it exists to enforce.
    ///     <para />
    ///     A snapshot already in flight satisfies the deadline. It has to: the request is answered by
    ///     the next pass and published by the turn after that, so for two turns the deadline is still
    ///     nominally past, and raising a second request in that window costs an extra full snapshot of
    ///     this server's population every interval, saying exactly what the first one said. Checked
    ///     under the outbox lock together with the set, so a snapshot being taken for delivery on
    ///     another thread cannot slip between the two.
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
    ///     held as a value rather than a pooled message because the wire message does not exist until
    ///     the batch is assembled.
    /// </summary>
    private readonly record struct PendingParcelChange(string Address, string Realm, ParcelCoord? Parcel);
}
