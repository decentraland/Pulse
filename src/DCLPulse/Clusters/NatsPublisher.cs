using Decentraland.Kernel.Comms.V3;
using Decentraland.Pulse;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;

namespace Pulse.Clusters;

/// <summary>
///     Sole owner of Pulse's NATS connection and the only component that talks to the broker.
///     Publish-only and fail-soft: producers hand messages to a coalescing outbox and never block, so
///     a slow, stalled or absent broker can delay the feed but never a tracker pass.
///     <para />
///     The outbox keeps the two feeds apart because they supersede differently.
///     <c>engine.islands</c> is a whole-world snapshot, so it lives in a single latest-wins slot and
///     never competes with an assignment. <c>peer.{addr}.cluster_change</c> supersedes per peer only,
///     so changes are held one per peer: a peer's newer assignment replaces its own older one, and one
///     peer's event never displaces another's — which a shared FIFO with oldest-first eviction could
///     not guarantee.
///     <para />
///     Genuine loss is therefore confined to more than <see cref="NatsOptions.ChannelCapacity" />
///     distinct peers pending at once, and is counted separately from benign superseding. With
///     <see cref="NatsOptions.Url" /> unset the service exits at startup and both
///     <see cref="IClusterFeedPublisher" /> methods become no-ops, leaving
///     <see cref="ClusterTracker" /> in stats-only mode.
///     <para />
///     Encoding is zero-copy: <see cref="SERIALIZER" /> writes a message straight into the buffer the
///     NATS client hands it, so the outbox holds live message instances rather than encoded bytes.
///     Instances are drawn from a free list per pooled type and returned by whoever takes one out, so
///     steady state neither rents a byte buffer nor copies a payload. What makes that sound is that
///     <c>NatsConnection.PublishAsync</c> has always finished serializing by the time the task it
///     returns completes — verified against NATS.Client.Core 3.0.1, whose <c>CommandWriter</c>
///     serializes synchronously ahead of its state machine and whose not-yet-connected path awaits the
///     connect and then serializes, both inside the awaited task.
/// </summary>
public sealed class NatsPublisher : BackgroundService, IClusterFeedPublisher
{
    // Clusters are uncapped, so zero advertises "no cap" rather than implying a bound that no
    // longer exists.
    private const uint NO_PEER_CAP = 0;

    // Stands in for a broker entry that is not a parseable absolute URL, so a typo is reported without
    // echoing a string that may hold credentials.
    private const string UNPARSED_BROKER_URL = "(unparsed broker url)";

    /// <summary>
    ///     Encodes every message this publisher sends, into the client's own buffer. One instance for
    ///     the process: <see cref="INatsSerialize{T}" /> is contravariant, so an
    ///     <see cref="IMessage" /> serializer covers both feed messages and the heartbeat, and no
    ///     serializer is allocated per publish. Internal because it alone decides what reaches the
    ///     wire, so the bytes it emits can be asserted without a broker.
    /// </summary>
    internal static readonly INatsSerialize<IMessage> SERIALIZER = new ProtobufSerializer();

    private readonly ILogger<NatsPublisher> logger;
    private readonly NatsOptions options;
    private readonly SnapshotBoard snapshotBoard;
    private readonly bool feedEnabled;

    // Outbox. Guarded by outboxLock: the tracker thread writes, the drain loop reads. Every entry
    // holds a message instance checked out of a free list, so anything that leaves the outbox —
    // superseded, evicted, dequeued or abandoned at shutdown — is returned by whoever took it out.
    private readonly Lock outboxLock = new ();
    private readonly Dictionary<string, PeerClusterChange> pendingChangeBySubject = new (StringComparer.Ordinal);
    private readonly Queue<string> changeOrder = new ();
    private IslandStatusMessage? pendingTopology;

    // Free lists for the two pooled message types, under outboxLock rather than a concurrent
    // structure: every rent and every return already sits next to an outbox mutation, so sharing the
    // lock keeps one ordering to reason about instead of two — and ConcurrentStack allocates a node
    // per push, which would put back the per-publish allocation the pooling exists to remove.
    //
    // Left unbounded because the number of live instances already is: the tracker thread holds at
    // most the one it is populating, the drain loop at most the one it is publishing, and the outbox
    // at most ChannelCapacity changes and one snapshot. Nothing can create more.
    private readonly Stack<PeerClusterChange> changePool = new ();
    private readonly Stack<IslandStatusMessage> topologyPool = new ();

    // Wake signal, not a queue: capacity 1 with DropWrite so repeated signals coalesce into one.
    private readonly Channel<byte>? wakeup;

    private readonly string clusterChangeSubjectPrefix;

    // Named "island", not "cluster", on purpose: engine.islands is archipelago's topology subject
    // carrying archipelago's IslandStatusMessage, and Pulse re-publishes that shape verbatim. The
    // island vocabulary survives only where it is archipelago's contract.
    private readonly string islandsSubject;

    private readonly string discoverySubject;
    private readonly string commitHash;

    // Topology scratch, reached from FillIslandStatus alone and therefore from the tracker thread
    // alone. islandById maps one pass's cluster ids to the islands just built, so its members are
    // filed in a single walk; islandPool holds the IslandData a shrinking snapshot no longer needs,
    // so a later one that grows again reuses them. An instance is only ever filled while no other
    // holder has it, so the islands reached through either of these belong to no live message.
    private readonly Dictionary<string, IslandData> islandById = new (StringComparer.Ordinal);
    private readonly Stack<IslandData> islandPool = new ();

    // The heartbeat's own instance, never entering the outbox. Its loop rewrites every field of the
    // message and of its status, so no beat repeats the last one's numbers, and it rewrites them only
    // after the previous publish's task has completed — by which point the client has serialized it.
    private readonly ServiceDiscoveryMessage discovery = new () { Status = new ServiceStatus() };

    private long publishedCount;
    private long droppedCount;
    private long supersededCount;
    private long reconnectCount;

    // Connection edges, held as ints so every transition is a single Interlocked exchange and each
    // edge emits its CONNECTED delta exactly once.
    private int connected;
    private int everConnected;

    public NatsPublisher(
        ILogger<NatsPublisher> logger,
        IOptions<NatsOptions> options,
        SnapshotBoard snapshotBoard)
    {
        this.logger = logger;
        this.options = options.Value;
        this.snapshotBoard = snapshotBoard;

        string prefix = this.options.SubjectPrefix;
        clusterChangeSubjectPrefix = $"{prefix}peer.";
        islandsSubject = $"{prefix}engine.islands";
        discoverySubject = $"{prefix}engine.discovery";
        commitHash = Environment.GetEnvironmentVariable("COMMIT_HASH") ?? "unknown";
        feedEnabled = this.options.IsConfigured;

        if (!feedEnabled) return;

        wakeup = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            // A pending signal already means "there is work"; a second adds nothing.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
    }

    /// <summary>
    ///     Messages published to the broker since startup.
    /// </summary>
    public long PublishedCount =>
        Interlocked.Read(ref publishedCount);

    /// <summary>
    ///     Messages genuinely lost — evicted because more than
    ///     <see cref="NatsOptions.ChannelCapacity" /> distinct peers were pending at once, or failed at
    ///     the broker. Unlike <see cref="SupersededCount" />, a non-zero rate here means a peer's
    ///     latest assignment never reached the broker.
    /// </summary>
    public long DroppedCount =>
        Interlocked.Read(ref droppedCount);

    /// <summary>
    ///     Messages replaced before delivery by a newer one for the same subject. Expected under
    ///     load and harmless — the newer message carries strictly fresher state.
    /// </summary>
    public long SupersededCount =>
        Interlocked.Read(ref supersededCount);

    /// <summary>
    ///     Times the client has re-established the connection after losing it.
    /// </summary>
    public long ReconnectCount =>
        Interlocked.Read(ref reconnectCount);

    /// <summary>
    ///     Whether the broker connection is currently up. False in stats-only mode.
    /// </summary>
    public bool IsConnected =>
        Volatile.Read(ref connected) == 1;

    public override void Dispose()
    {
        base.Dispose();

        // Last return path: whatever the drain loop never got to is still checked out.
        ReturnPending();
    }

    /// <summary>
    ///     Hands every still-pending message back to its free list and empties the outbox. Idempotent
    ///     — each entry is dropped as it is returned, so a second call finds nothing.
    /// </summary>
    private void ReturnPending()
    {
        lock (outboxLock)
        {
            if (pendingTopology is { } abandoned)
            {
                pendingTopology = null;
                topologyPool.Push(abandoned);
            }

            foreach (KeyValuePair<string, PeerClusterChange> pending in pendingChangeBySubject)
                changePool.Push(pending.Value);

            pendingChangeBySubject.Clear();
            changeOrder.Clear();
        }
    }

    public void PublishClusterChange(string wallet, string clusterId, string realm)
    {
        if (!feedEnabled) return;

        // Nulled in the same step that hands the instance to the outbox, so the catch below can only
        // ever return one the outbox did not take.
        PeerClusterChange? rented = RentChange();

        try
        {
            // Both of the rented message's fields are rewritten, so nothing survives the previous peer.
            rented.ClusterId = clusterId;
            rented.Realm = realm;

            // Lower-cased so one wallet always maps to one subject, whatever checksum casing the auth
            // chain carried. The subject is also the coalescing key, so per-subject latest-wins is
            // exactly per-peer latest-wins.
            var subject = $"{clusterChangeSubjectPrefix}{wallet.ToLowerInvariant()}.cluster_change";

            PeerClusterChange change = rented;
            rented = null;

            QueueChange(subject, change);
        }
        catch (Exception e)
        {
            if (rented is { } unqueued)
                ReturnChange(unqueued);

            logger.LogWarning(e, "Failed to publish cluster change for {ClusterId}; dropping", clusterId);
        }
    }

    public void PublishTopology(ClusterPass pass)
    {
        if (!feedEnabled) return;

        // Nulled on hand-off for the same reason as in PublishClusterChange.
        IslandStatusMessage? rented = RentTopology();

        try
        {
            FillIslandStatus(rented, pass);

            IslandStatusMessage snapshot = rented;
            rented = null;

            var superseded = false;

            lock (outboxLock)
            {
                // Latest wins: an undelivered snapshot is worthless once a newer one exists. The
                // replaced instance is returned in the same step that stops naming it, so the free
                // list can never hold one the slot still points at.
                if (pendingTopology is { } replaced)
                {
                    topologyPool.Push(replaced);
                    superseded = true;
                }

                pendingTopology = snapshot;
            }

            // Counted after the lock is released: Counter.Add runs every registered MeterListener
            // callback inline, and the outbox lock is what the drain loop needs to make progress.
            if (superseded)
                CountSuperseded();

            Signal();
        }
        catch (Exception e)
        {
            if (rented is { } unqueued)
                ReturnTopology(unqueued);

            logger.LogWarning(e, "Failed to publish cluster topology; dropping");
        }
    }

    /// <summary>
    ///     A <see cref="PeerClusterChange" /> to populate, reused when the free list has one. Its
    ///     fields still hold the previous occupant's assignment until the caller rewrites them.
    /// </summary>
    private PeerClusterChange RentChange()
    {
        lock (outboxLock)
            return changePool.TryPop(out PeerClusterChange? pooled) ? pooled : new PeerClusterChange();
    }

    /// <summary>
    ///     Hands one change back for reuse — the counterpart of a single <see cref="RentChange" />.
    /// </summary>
    private void ReturnChange(PeerClusterChange change)
    {
        lock (outboxLock)
            changePool.Push(change);
    }

    /// <summary>
    ///     An <see cref="IslandStatusMessage" /> to fill, reused when the free list has one. It still
    ///     carries the pass it last held, islands included, until <see cref="FillIslandStatus" />
    ///     rewrites it.
    /// </summary>
    private IslandStatusMessage RentTopology()
    {
        lock (outboxLock)
            return topologyPool.TryPop(out IslandStatusMessage? pooled) ? pooled : new IslandStatusMessage();
    }

    /// <summary>
    ///     Hands one snapshot back for reuse — the counterpart of a single <see cref="RentTopology" />.
    /// </summary>
    private void ReturnTopology(IslandStatusMessage snapshot)
    {
        lock (outboxLock)
            topologyPool.Push(snapshot);
    }

    /// <summary>
    ///     Takes ownership of <paramref name="change" /> and holds at most one pending change per peer.
    ///     A repeat for the same peer replaces its message and keeps its place in line; a new peer past
    ///     capacity evicts the longest-waiting one, the only path to genuine loss. Whichever instance
    ///     the outbox lets go of is returned to the free list here.
    /// </summary>
    private void QueueChange(string subject, PeerClusterChange change)
    {
        bool superseded;
        var dropped = false;

        lock (outboxLock)
        {
            if (pendingChangeBySubject.TryGetValue(subject, out PeerClusterChange? previous))
            {
                superseded = true;

                // Replaced and returned in one step, so the free list cannot hold an instance the
                // outbox still names.
                pendingChangeBySubject[subject] = change;
                changePool.Push(previous);
            }
            else
            {
                superseded = false;

                if (pendingChangeBySubject.Count >= options.ChannelCapacity && changeOrder.TryDequeue(out string? evicted))
                {
                    // Taken out with its message in one step, for the same reason.
                    if (pendingChangeBySubject.Remove(evicted, out PeerClusterChange? lost))
                        changePool.Push(lost);

                    dropped = true;
                }

                pendingChangeBySubject[subject] = change;
                changeOrder.Enqueue(subject);
            }
        }

        // Counted after the lock is released: Counter.Add runs every registered MeterListener callback
        // inline, and the outbox lock is what the drain loop needs to make progress. The two outcomes
        // are exclusive — an eviction only happens on the branch that admits a new subject.
        if (superseded)
            CountSuperseded();
        else if (dropped)
            CountDropped();

        Signal();
    }

    /// <summary>
    ///     Takes the next message to deliver: the topology first, so a snapshot declaring a cluster
    ///     precedes the per-peer events that reference it, then peers in arrival order. Ownership of the
    ///     instance moves to the caller — the outbox drops its own reference in the same step — so the
    ///     caller is the one that must return it. Internal so the ordering can be asserted without a
    ///     broker.
    /// </summary>
    internal bool TryDequeueNext(out string subject, [NotNullWhen(true)] out IMessage? message)
    {
        lock (outboxLock)
        {
            if (pendingTopology is { } pending)
            {
                subject = islandsSubject;
                message = pending;
                pendingTopology = null;

                return true;
            }

            while (changeOrder.TryDequeue(out string? next))
            {
                if (!pendingChangeBySubject.Remove(next, out PeerClusterChange? queued)) continue;

                subject = next;
                message = queued;

                return true;
            }
        }

        subject = string.Empty;
        message = null;

        return false;
    }

    /// <summary>
    ///     Hands a dequeued message back to the free list its type is pooled in. Reached once per
    ///     dequeue: <see cref="TryDequeueNext" /> drops the outbox's own reference as it hands the
    ///     message over, so its caller is the only holder there is. Internal for the same reason as
    ///     <see cref="TryDequeueNext" /> — it is the other half of the hand-off, and without it the
    ///     instance lifecycle cannot be driven without a broker.
    /// </summary>
    internal void Return(IMessage message)
    {
        lock (outboxLock)
        {
            switch (message)
            {
                case PeerClusterChange change:
                    changePool.Push(change);
                    break;
                case IslandStatusMessage snapshot:
                    topologyPool.Push(snapshot);
                    break;
            }
        }
    }

    private void Signal()
    {
        wakeup?.Writer.TryWrite(0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (wakeup is null)
        {
            logger.LogInformation("NATS feed disabled (Nats:Url not set) — cluster tracker runs in stats-only mode");
            return;
        }

        // A non-positive interval is not a valid timer period, so the heartbeat is skipped rather
        // than allowed to abort the publisher; the outbox drain still runs.
        bool heartbeatEnabled = options.DiscoveryIntervalMs > 0;

        if (!heartbeatEnabled)
            logger.LogWarning("NATS discovery heartbeat disabled (Nats:DiscoveryIntervalMs is not positive)");

        var opts = NatsOpts.Default with { Url = options.Url, Name = options.ServerName };

        await using var connection = new NatsConnection(opts);

        connection.ConnectionOpened += OnConnectionOpened;
        connection.ConnectionDisconnected += OnConnectionDisconnected;

        // Both loops run until cancelled, so neither ever completes on its own. Linking them means a
        // fault in one stops the other instead of leaving half the feed running silently.
        using var loops = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        try
        {
            logger.LogInformation("NATS publisher started — {Broker}, subject prefix {SubjectPrefix}",
                SanitizeBrokerUrl(options.Url),
                string.IsNullOrEmpty(options.SubjectPrefix) ? "(none)" : options.SubjectPrefix);

            await Task.WhenAll(
                DrainAsync(connection, wakeup, loops),
                heartbeatEnabled ? PublishDiscoveryPeriodicallyAsync(connection, loops) : Task.CompletedTask);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception e)
        {
            logger.LogError(e, "NATS publisher stopped unexpectedly; the cluster feed is no longer being delivered");
        }
        finally
        {
            connection.ConnectionOpened -= OnConnectionOpened;
            connection.ConnectionDisconnected -= OnConnectionDisconnected;
            MarkDisconnected();
        }
    }

    /// <summary>
    ///     Reduces a broker address to host and port. A NATS address may be a comma-separated seed list
    ///     (<c>nats://a:4222,nats://b:4222</c>), so every entry is reduced and the results rejoined;
    ///     an address naming no entry at all yields <see cref="UNPARSED_BROKER_URL" />.
    ///     <para />
    ///     Internal so the redaction can be asserted directly: the address is an injected secret and
    ///     whatever this returns is logged verbatim.
    /// </summary>
    internal static string SanitizeBrokerUrl(string url)
    {
        var sanitized = new StringBuilder();

        foreach (string entry in url.Split(','))
        {
            string trimmed = entry.Trim();

            // An empty entry comes from a leading, trailing or doubled comma and names no broker.
            if (trimmed.Length == 0) continue;

            if (sanitized.Length > 0)
                sanitized.Append(", ");

            sanitized.Append(SanitizeBrokerEntry(trimmed));
        }

        return sanitized.Length == 0 ? UNPARSED_BROKER_URL : sanitized.ToString();
    }

    /// <summary>
    ///     Reduces one broker URL to host and port, dropping the userinfo a NATS URL may carry
    ///     (<c>nats://user:password@host:4222</c>) along with everything else outside the authority. An
    ///     entry that is not a parseable absolute URL yields <see cref="UNPARSED_BROKER_URL" /> rather
    ///     than the original string.
    /// </summary>
    private static string SanitizeBrokerEntry(string entry)
    {
        if (!Uri.TryCreate(entry, UriKind.Absolute, out Uri? parsed) || string.IsNullOrEmpty(parsed.Host))
            return UNPARSED_BROKER_URL;

        return parsed.IsDefaultPort ? parsed.Host : $"{parsed.Host}:{parsed.Port}";
    }

    /// <summary>
    ///     Drains the outbox to the broker, woken by <see cref="Signal" />. A failed publish is
    ///     logged and abandoned: the message is stale by the next pass anyway, and retrying would
    ///     grow the backlog. A fault that ends the loop is logged once and cancels
    ///     <paramref name="loops" />.
    /// </summary>
    private async Task DrainAsync(NatsConnection connection, Channel<byte> signal, CancellationTokenSource loops)
    {
        CancellationToken token = loops.Token;

        try
        {
            await foreach (byte _ in signal.Reader.ReadAllAsync(token))
            {
                while (TryDequeueNext(out string subject, out IMessage? message))
                {
                    try
                    {
                        await connection.PublishAsync(
                            subject, message, serializer: SERIALIZER, cancellationToken: token);

                        CountPublished();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        CountDropped();
                        logger.LogWarning(e, "Failed to publish to {Subject}; dropping", subject);
                    }
                    finally
                    {
                        // The dequeue handed ownership over, so every exit from the publish returns it
                        // exactly once — delivered, failed, or cancelled. The awaited task does not
                        // complete until the client has serialized the message, so no path here hands
                        // back an instance the client is still reading.
                        Return(message);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown, or the sibling loop faulted and cancelled this one.
        }
        catch (Exception e)
        {
            logger.LogError(e, "NATS outbox drain stopped unexpectedly; the cluster feed is no longer being delivered");
            loops.Cancel();
        }
    }

    /// <summary>
    ///     Publishes the <c>engine.discovery</c> heartbeat on a fixed cadence. Sent directly rather
    ///     than through the outbox: a heartbeat delayed behind a backlog would defeat its purpose. A
    ///     fault that ends the loop is logged once and cancels <paramref name="loops" />.
    /// </summary>
    private async Task PublishDiscoveryPeriodicallyAsync(NatsConnection connection, CancellationTokenSource loops)
    {
        CancellationToken token = loops.Token;

        try
        {
            var interval = TimeSpan.FromMilliseconds(options.DiscoveryIntervalMs);
            using var timer = new PeriodicTimer(interval);

            while (!token.IsCancellationRequested)
            {
                // Every field of the reused message and of its status, so no beat repeats the last one's
                // numbers. This instance belongs to this loop alone.
                discovery.ServerName = options.ServerName;
                discovery.Status.CurrentTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                discovery.Status.CommitHash = commitHash;
                discovery.Status.UserCount = (uint)CountActivePeers();

                try
                {
                    await connection.PublishAsync(
                        discoverySubject, discovery, serializer: SERIALIZER, cancellationToken: token);

                    CountPublished();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to publish service discovery heartbeat");
                }

                try { await timer.WaitForNextTickAsync(token); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown, or the sibling loop faulted and cancelled this one.
        }
        catch (Exception e)
        {
            logger.LogError(e, "NATS discovery heartbeat stopped unexpectedly; the service is no longer advertised");
            loops.Cancel();
        }
    }

    private int CountActivePeers()
    {
        var count = 0;

        foreach (PeerIndex _ in snapshotBoard.GetActivePeers())
            count++;

        return count;
    }

    private void CountPublished()
    {
        Interlocked.Increment(ref publishedCount);
        PulseMetrics.Nats.PUBLISHED.Add(1);
    }

    private void CountDropped()
    {
        Interlocked.Increment(ref droppedCount);
        PulseMetrics.Nats.DROPPED.Add(1);
    }

    private void CountSuperseded()
    {
        Interlocked.Increment(ref supersededCount);
        PulseMetrics.Nats.SUPERSEDED.Add(1);
    }

    /// <summary>
    ///     Projects a pass onto <paramref name="snapshot" />, in <c>IslandStatusMessage</c> — the shape
    ///     archipelago-core published. Each island in use has all five of its fields rewritten and its
    ///     peer list emptied, and the message carries only its island list, so nothing survives the
    ///     pass this instance last held.
    /// </summary>
    private void FillIslandStatus(IslandStatusMessage snapshot, ClusterPass pass)
    {
        islandById.Clear();

        RepeatedField<IslandData> data = snapshot.Data;

        // The islands still attached belong to the pass this instance last held, so they go back to the
        // free list before the list is emptied rather than being dropped on the floor.
        for (var i = 0; i < data.Count; i++)
            islandPool.Push(data[i]);

        // RepeatedField.Clear keeps its backing array, so refilling the list costs nothing.
        data.Clear();

        for (var i = 0; i < pass.Clusters.Count; i++)
        {
            ClusterInfo cluster = pass.Clusters[i];
            IslandData island = RentIsland();

            island.Id = cluster.Id;
            island.MaxPeers = NO_PEER_CAP;
            island.Radius = cluster.Radius;
            island.Center.X = cluster.Centroid.X;
            island.Center.Y = cluster.Centroid.Y;
            island.Center.Z = cluster.Centroid.Z;
            island.Peers.Clear();

            islandById[cluster.Id] = island;
            data.Add(island);
        }

        for (var i = 0; i < pass.Peers.Count; i++)
        {
            ClusterPeerInfo peer = pass.Peers[i];

            if (islandById.TryGetValue(peer.ClusterId, out IslandData? island))
                island.Peers.Add(peer.Wallet);
        }
    }

    /// <summary>
    ///     An <see cref="IslandData" /> to fill, reused when the free list has one and created together
    ///     with its center otherwise. Its fields carry an earlier pass's values until the caller
    ///     rewrites them.
    /// </summary>
    private IslandData RentIsland() =>
        islandPool.TryPop(out IslandData? pooled)
            ? pooled
            : new IslandData { Center = new Decentraland.Common.Position() };

    /// <summary>
    ///     Counts every open past the first as a reconnect and records the connection as up. Internal so
    ///     the connection edges — and with them the ± pairing of <c>pulse.nats.connected</c> — can be
    ///     driven without a broker.
    /// </summary>
    internal ValueTask OnConnectionOpened(object? sender, NatsEventArgs args)
    {
        // Every open after the first one is by definition a reconnect. Tested and set in one step, so
        // two opens racing cannot both read themselves as the first.
        if (Interlocked.Exchange(ref everConnected, 1) == 1)
        {
            Interlocked.Increment(ref reconnectCount);
            PulseMetrics.Nats.RECONNECTS.Add(1);
        }

        MarkConnected();

        logger.LogInformation("NATS connection opened");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Records the connection as up, adding +1 to <c>pulse.nats.connected</c> only when this call
    ///     is the one that changed the flag. Idempotent — a repeat while already up adds nothing.
    /// </summary>
    private void MarkConnected()
    {
        if (Interlocked.Exchange(ref connected, 1) == 0)
            PulseMetrics.Nats.CONNECTED.Add(1);
    }

    /// <summary>
    ///     Records the connection as down. Internal for the same reason as
    ///     <see cref="OnConnectionOpened" /> — the losing edge is half of the gauge pairing.
    /// </summary>
    internal ValueTask OnConnectionDisconnected(object? sender, NatsEventArgs args)
    {
        MarkDisconnected();

        logger.LogWarning(
            "NATS connection lost; the topology and each peer's latest assignment are retained until it returns");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Records the connection as down, adding -1 to <c>pulse.nats.connected</c> only when this call
    ///     is the one that changed the flag. Idempotent — a repeat while already down adds nothing, so
    ///     the +1 and the -1 stay paired however many times either transition is signalled.
    /// </summary>
    private void MarkDisconnected()
    {
        if (Interlocked.Exchange(ref connected, 0) == 1)
            PulseMetrics.Nats.CONNECTED.Add(-1);
    }

    /// <summary>
    ///     Writes a protobuf message into the buffer the NATS client supplies, which is the client's own
    ///     pooled writer — so a publish neither rents a buffer of its own nor copies the encoded bytes
    ///     into one.
    /// </summary>
    private sealed class ProtobufSerializer : INatsSerialize<IMessage>
    {
        public void Serialize(IBufferWriter<byte> bufferWriter, IMessage value) =>
            value.WriteTo(bufferWriter);
    }
}
