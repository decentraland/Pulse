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
///     Publish-only and fail-soft by construction: producers hand messages to a bounded channel and
///     never block, so a slow, stalled or absent broker can delay the feed but never the tracker
///     pass or the simulation.
///     <para />
///     On overflow the oldest queued message is evicted rather than the newest. Every subject here
///     is self-superseding — a newer assignment or topology snapshot fully replaces an older one —
///     so dropping the stale end preserves the most useful state.
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
    private readonly Channel<FeedMessage>? channel;
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

        if (!this.options.IsConfigured) return;

        channel = Channel.CreateBounded<FeedMessage>(new BoundedChannelOptions(this.options.ChannelCapacity)
        {
            // Wait rather than DropOldest: eviction is done explicitly in Enqueue so drops can be
            // counted, which BoundedChannelFullMode does not report.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// <summary>
    ///     Messages published to the broker since startup.
    /// </summary>
    public long PublishedCount =>
        Interlocked.Read(ref publishedCount);

    /// <summary>
    ///     Messages evicted from the queue because the broker could not keep up.
    /// </summary>
    public long DroppedCount =>
        Interlocked.Read(ref droppedCount);

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
        if (channel is null) return;

        try
        {
            var message = new PeerClusterChange { ClusterId = clusterId, Realm = realm };

            // Wallets are lower-cased so one wallet always maps to one subject, regardless of the
            // checksum casing the auth chain happened to carry.
            Enqueue(channel, new FeedMessage(
                $"{clusterChangeSubjectPrefix}{wallet.ToLowerInvariant()}.cluster_change",
                message.ToByteArray()));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to encode cluster change for {ClusterId}; dropping", clusterId);
        }
    }

    public void PublishTopology(ClusterPass pass)
    {
        if (channel is null) return;

        try
        {
            Enqueue(channel, new FeedMessage(islandsSubject, BuildIslandStatus(pass).ToByteArray()));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to encode cluster topology; dropping");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (channel is null)
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
                DrainAsync(connection, stoppingToken),
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
    ///     Drains the hand-off channel to the broker. A failed publish is logged and abandoned:
    ///     the message is stale by the next pass anyway, and retrying would grow the backlog.
    /// </summary>
    private async Task DrainAsync(NatsConnection connection, CancellationToken stoppingToken)
    {
        await foreach (FeedMessage message in channel!.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await connection.PublishAsync(message.Subject, message.Payload, cancellationToken: stoppingToken);
                CountPublished();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                CountDropped();
                logger.LogWarning(e, "Failed to publish to {Subject}; dropping", message.Subject);
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

    /// <summary>
    ///     Queues a message, evicting the oldest entries to make room. Bounded by the channel
    ///     capacity so it always terminates even while a producer races the reader.
    /// </summary>
    private void Enqueue(Channel<FeedMessage> queue, FeedMessage message)
    {
        for (var attempt = 0; attempt <= options.ChannelCapacity; attempt++)
        {
            if (queue.Writer.TryWrite(message)) return;

            if (!queue.Reader.TryRead(out _)) break;

            CountDropped();
        }

        // Never queued: the reader drained nothing and every attempt lost the race.
        CountDropped();
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

        logger.LogWarning("NATS connection lost; queued messages will be dropped oldest-first until it returns");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     A serialized message awaiting delivery. Encoding happens on the producer thread so the
    ///     queue never retains a reference to a live pass result.
    /// </summary>
    private readonly record struct FeedMessage(string Subject, byte[] Payload);
}
