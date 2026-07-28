using Decentraland.Kernel.Comms.V3;
using Decentraland.Pulse;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Threading.Channels;

namespace Pulse.Clusters;

/// <summary>
///     Sole owner of Pulse's NATS connection and the only component that talks to the broker.
///     Publish-only and fail-soft by construction: producers hand messages to a coalescing outbox and
///     never block, so a slow, stalled or absent broker can delay the feed but never the tracker
///     pass or the simulation.
///     <para />
///     The outbox keeps the two feeds apart, because they supersede differently:
///     <list type="bullet">
///         <item>
///             <c>engine.islands</c> is a whole-world snapshot — a newer one fully replaces an older
///             one, so it lives in a single latest-wins slot and can never occupy more than one
///             delivery slot or crowd out an assignment.
///         </item>
///         <item>
///             <c>peer.{addr}.cluster_change</c> supersedes only <b>per peer</b>. Two peers' events
///             carry disjoint information, so they are held one-per-peer: a peer's newer assignment
///             replaces its own older one, and one peer's event can never displace another's.
///         </item>
///     </list>
///     A single shared FIFO with oldest-first eviction would have been wrong for the second case —
///     it could discard peer A's assignment to make room for peer B's, leaving A addressed by a stale
///     cluster until its next reassignment, which may never come if A stops moving.
///     <para />
///     Genuine loss is therefore confined to sustained overload with more than
///     <see cref="NatsOptions.ChannelCapacity" /> distinct peers pending at once, and is counted
///     separately from benign superseding.
///     <para />
///     With <see cref="NatsOptions.Url" /> unset the service exits at startup and both
///     <see cref="IClusterFeedPublisher" /> methods degrade to constant-time no-ops, leaving
///     <see cref="ClusterTracker" /> running in stats-only mode. That is the rollback path.
/// </summary>
public sealed class NatsPublisher : BackgroundService, IClusterFeedPublisher
{
    // Clusters are uncapped by design — clustering no longer blocks merges at a room size, and
    // room sharding is the token issuer's concern. Zero advertises "no cap" to consumers rather
    // than implying a bound that no longer exists.
    private const uint NO_PEER_CAP = 0;

    private readonly ILogger<NatsPublisher> logger;
    private readonly NatsOptions options;
    private readonly SnapshotBoard snapshotBoard;
    private readonly bool feedEnabled;

    // Outbox. Guarded by outboxLock: the tracker thread writes, the drain loop reads.
    private readonly Lock outboxLock = new ();
    private readonly Dictionary<string, byte[]> pendingChangeBySubject = new (StringComparer.Ordinal);
    private readonly Queue<string> changeOrder = new ();
    private byte[]? pendingTopology;

    // Wake signal, not a queue: capacity 1 with DropWrite so repeated signals coalesce into one.
    private readonly Channel<byte>? wakeup;

    private readonly string clusterChangeSubjectPrefix;

    // Named "island", not "cluster", on purpose: engine.islands is archipelago's topology subject
    // carrying archipelago's IslandStatusMessage. Pulse re-publishes that shape verbatim so
    // archipelago-stats keeps working after core is decommissioned. Pulse's own concept is the
    // cluster; the island vocabulary survives only where it is archipelago's contract.
    private readonly string islandsSubject;

    private readonly string discoverySubject;
    private readonly string commitHash;

    private long publishedCount;
    private long droppedCount;
    private long supersededCount;
    private long reconnectCount;
    private volatile bool connected;
    private bool everConnected;

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
    ///     <see cref="NatsOptions.ChannelCapacity" /> distinct peers were pending at once, or failed
    ///     at the broker. Distinct from <see cref="SupersededCount" />: a non-zero rate here means a
    ///     peer may be addressed by a stale cluster.
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
        connected;

    public void PublishClusterChange(string wallet, string clusterId, string realm)
    {
        if (!feedEnabled) return;

        try
        {
            var message = new PeerClusterChange { ClusterId = clusterId, Realm = realm };

            // Wallets are lower-cased so one wallet always maps to one subject, regardless of the
            // checksum casing the auth chain happened to carry. The subject is also the coalescing
            // key, so per-subject latest-wins is exactly per-peer latest-wins.
            QueueChange(
                $"{clusterChangeSubjectPrefix}{wallet.ToLowerInvariant()}.cluster_change",
                message.ToByteArray());
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to encode cluster change for {ClusterId}; dropping", clusterId);
        }
    }

    public void PublishTopology(ClusterPass pass)
    {
        if (!feedEnabled) return;

        try
        {
            byte[] payload = BuildIslandStatus(pass).ToByteArray();

            lock (outboxLock)
            {
                // Latest wins: an undelivered snapshot is worthless once a newer one exists.
                if (pendingTopology is not null)
                    CountSuperseded();

                pendingTopology = payload;
            }

            Signal();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to encode cluster topology; dropping");
        }
    }

    /// <summary>
    ///     Holds at most one pending change per peer. A repeat for the same peer replaces its payload
    ///     and keeps its place in line; a new peer past capacity evicts the longest-waiting peer,
    ///     which is the only path to genuine loss.
    /// </summary>
    private void QueueChange(string subject, byte[] payload)
    {
        lock (outboxLock)
        {
            if (pendingChangeBySubject.ContainsKey(subject))
            {
                CountSuperseded();
                pendingChangeBySubject[subject] = payload;
            }
            else
            {
                if (pendingChangeBySubject.Count >= options.ChannelCapacity && changeOrder.TryDequeue(out string? evicted))
                {
                    pendingChangeBySubject.Remove(evicted);
                    CountDropped();
                }

                pendingChangeBySubject[subject] = payload;
                changeOrder.Enqueue(subject);
            }
        }

        Signal();
    }

    /// <summary>
    ///     Takes the next message to deliver: the topology first, so a snapshot declaring a cluster
    ///     precedes the per-peer events that reference it, then peers in arrival order.
    ///     Internal so the ordering guarantee can be asserted without a broker.
    /// </summary>
    internal bool TryDequeueNext(out string subject, out byte[] payload)
    {
        lock (outboxLock)
        {
            if (pendingTopology is not null)
            {
                subject = islandsSubject;
                payload = pendingTopology;
                pendingTopology = null;

                return true;
            }

            while (changeOrder.TryDequeue(out string? next))
            {
                if (!pendingChangeBySubject.Remove(next, out byte[]? queued)) continue;

                subject = next;
                payload = queued;

                return true;
            }
        }

        subject = string.Empty;
        payload = [];

        return false;
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

        var opts = NatsOpts.Default with { Url = options.Url, Name = options.ServerName };

        await using var connection = new NatsConnection(opts);

        connection.ConnectionOpened += OnConnectionOpened;
        connection.ConnectionDisconnected += OnConnectionDisconnected;

        try
        {
            logger.LogInformation("NATS publisher started — {Url}, subject prefix {SubjectPrefix}",
                options.Url, string.IsNullOrEmpty(options.SubjectPrefix) ? "(none)" : options.SubjectPrefix);

            await Task.WhenAll(
                DrainAsync(connection, wakeup, stoppingToken),
                PublishDiscoveryPeriodicallyAsync(connection, stoppingToken));
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
            connected = false;
        }
    }

    /// <summary>
    ///     Drains the outbox to the broker, woken by <see cref="Signal" />. A failed publish is
    ///     logged and abandoned: the message is stale by the next pass anyway, and retrying would
    ///     grow the backlog.
    /// </summary>
    private async Task DrainAsync(NatsConnection connection, Channel<byte> signal, CancellationToken stoppingToken)
    {
        await foreach (byte _ in signal.Reader.ReadAllAsync(stoppingToken))
        {
            while (TryDequeueNext(out string subject, out byte[] payload))
            {
                try
                {
                    await connection.PublishAsync(subject, payload, cancellationToken: stoppingToken);
                    CountPublished();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    CountDropped();
                    logger.LogWarning(e, "Failed to publish to {Subject}; dropping", subject);
                }
            }
        }
    }

    /// <summary>
    ///     Heartbeat that keeps archipelago-stats considering the service healthy after
    ///     archipelago-core is decommissioned. Published directly rather than through the channel:
    ///     a heartbeat delayed behind a backlog would defeat its purpose.
    /// </summary>
    private async Task PublishDiscoveryPeriodicallyAsync(NatsConnection connection, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.DiscoveryIntervalMs);
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var message = new ServiceDiscoveryMessage
            {
                ServerName = options.ServerName,
                Status = new ServiceStatus
                {
                    CurrentTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    CommitHash = commitHash,
                    UserCount = (uint)CountActivePeers(),
                },
            };

            try
            {
                await connection.PublishAsync(discoverySubject, message.ToByteArray(), cancellationToken: stoppingToken);
                CountPublished();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to publish service discovery heartbeat");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
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
    ///     Projects a pass onto the message archipelago-core published, so the stats service keeps
    ///     working unchanged after core is removed.
    /// </summary>
    private static IslandStatusMessage BuildIslandStatus(ClusterPass pass)
    {
        var message = new IslandStatusMessage();
        var dataById = new Dictionary<string, IslandData>(pass.Clusters.Count, StringComparer.Ordinal);

        for (var i = 0; i < pass.Clusters.Count; i++)
        {
            ClusterInfo cluster = pass.Clusters[i];

            var data = new IslandData
            {
                Id = cluster.Id,
                MaxPeers = NO_PEER_CAP,
                Radius = cluster.Radius,
                Center = new Decentraland.Common.Position
                {
                    X = cluster.Centroid.X,
                    Y = cluster.Centroid.Y,
                    Z = cluster.Centroid.Z,
                },
            };

            dataById[cluster.Id] = data;
            message.Data.Add(data);
        }

        for (var i = 0; i < pass.Peers.Count; i++)
        {
            ClusterPeerInfo peer = pass.Peers[i];

            if (dataById.TryGetValue(peer.ClusterId, out IslandData? data))
                data.Peers.Add(peer.Wallet);
        }

        return message;
    }

    private ValueTask OnConnectionOpened(object? sender, NatsEventArgs args)
    {
        // Every open after the first one is by definition a reconnect.
        if (everConnected)
        {
            Interlocked.Increment(ref reconnectCount);
            PulseMetrics.Nats.RECONNECTS.Add(1);
        }

        everConnected = true;

        if (!connected)
        {
            connected = true;
            PulseMetrics.Nats.CONNECTED.Add(1);
        }

        logger.LogInformation("NATS connection opened");

        return ValueTask.CompletedTask;
    }

    private ValueTask OnConnectionDisconnected(object? sender, NatsEventArgs args)
    {
        if (connected)
        {
            connected = false;
            PulseMetrics.Nats.CONNECTED.Add(-1);
        }

        logger.LogWarning(
            "NATS connection lost; the topology and each peer's latest assignment are retained until it returns");

        return ValueTask.CompletedTask;
    }
}
