using System.Diagnostics.Metrics;
using Pulse.Messaging;
using Pulse.Transport;
using Pulse.Transport.Geo;
using Pulse.Transport.Hardening;

namespace Pulse.Metrics;

/// <summary>
///     Subscribes to <see cref="PulseMetrics" /> instruments via <see cref="MeterListener" />
///     and accumulates raw counters. Consumers pull snapshots on demand via <see cref="TakeSnapshot" />.
///     Threading model:
///     - MeterListener callbacks fire on the recording thread (ENet / WebTransport / worker threads).
///       They do a single Interlocked.Add — minimal overhead on the hot path.
///     - <see cref="TakeSnapshot" /> is called by consumers on their own schedule.
///     Transport instruments carry a <c>transport</c> tag (see <see cref="PulseMetrics.Transport.Tag" />);
///     the callbacks bucket each measurement by <see cref="TransportId" /> read from that tag.
/// </summary>
public sealed class MeterListenerMetricsCollector : IMetricsCollector, IHostedService, IDisposable
{
    private static readonly int TRANSPORT_COUNT = Enum.GetValues<TransportId>().Length;

    private readonly MessagePipe messagePipe;
    private readonly ClientMessageCounters incomingMessageCounters;
    private readonly ServerMessageCounters outgoingMessageCounters;
    private readonly MeterListener listener;

    // Per-transport transport totals, indexed by (int)TransportId — written by MeterListener callbacks
    // on recording threads, one Interlocked.Add per measurement.
    private readonly long[] peersConnected = new long[TRANSPORT_COUNT];
    private readonly long[] peersDisconnected = new long[TRANSPORT_COUNT];
    private readonly int[] activePeers = new int[TRANSPORT_COUNT];
    private readonly long[] bytesReceived = new long[TRANSPORT_COUNT];
    private readonly long[] bytesSent = new long[TRANSPORT_COUNT];
    private readonly long[] packetsReceived = new long[TRANSPORT_COUNT];
    private readonly long[] packetsSent = new long[TRANSPORT_COUNT];
    private readonly long[] unauthMessagesSkipped = new long[TRANSPORT_COUNT];
    private readonly long[] sendFailures = new long[TRANSPORT_COUNT];

    // Per-connection-class per-IP refusals, indexed by (int)ConnectionClass — the counter carries a
    // class tag, so the callback buckets each measurement the way transport totals are bucketed.
    private readonly long[] ipLimitRefused = new long[ConnectionClasses.COUNT];

    // WebTransport-specific totals (no ENet analogue, so not per-transport).
    private long datagramsDroppedStale;
    private long datagramsDroppedOversize;

    // Hardening totals.
    private long preAuthIpLimitRefused;
    private long preAuthRefused;
    private long handshakeAttemptsExceeded;
    private int preAuthInFlight;
    private long inputRateThrottled;
    private long discreteEventThrottled;
    private long fieldValidationFailed;
    private long handshakeReplayRejected;
    private long bannedRefused;
    private long corruptedPacket;
    private long ipLimitWhitelistBypass;
    private int ipLimitTrackedIps;

    // Scene-listener totals.
    private int sceneListenersConnected;
    private long sceneListenerForbiddenMessagesDropped;
    private long sceneListenerVisibleSubjectsSum;
    private long sceneListenerVisibleSubjectsCount;

    // Latency histograms — bucketed by the measurement callbacks on recording threads.
    private readonly BucketHistogram deltaStalenessTier0 = new (PulseMetrics.Simulation.STALENESS_BUCKETS_MS);
    private readonly BucketHistogram deltaStalenessTier1 = new (PulseMetrics.Simulation.STALENESS_BUCKETS_MS);
    private readonly BucketHistogram deltaStalenessTier2 = new (PulseMetrics.Simulation.STALENESS_BUCKETS_MS);
    private readonly BucketHistogram tickDurationUs = new (PulseMetrics.Simulation.DURATION_BUCKETS_US);
    private readonly BucketHistogram outgoingDrainCycleUs = new (PulseMetrics.Simulation.DURATION_BUCKETS_US);
    private long tickOverruns;

    // Per-continent peer RTT histograms — indexed by (int)Continent.
    private readonly BucketHistogram[] peerRttMs = CreatePeerRttHistograms();

    private static BucketHistogram[] CreatePeerRttHistograms()
    {
        var histograms = new BucketHistogram[Continents.COUNT];

        for (var i = 0; i < histograms.Length; i++)
            histograms[i] = new BucketHistogram(PulseMetrics.Transport.RTT_BUCKETS_MS);

        return histograms;
    }

    // Cluster derivation and feed totals — recorded once per pass on the tracker thread.
    private int clusterCount;
    private int clusterPeers;
    private int clusterSizeMax;

    // Cluster-size histogram: non-cumulative per-bucket counts plus the sum/count pair. The formatter
    // turns the counts into the cumulative form Prometheus expects.
    private readonly BucketHistogram clusterSize = new (PulseMetrics.Clusters.SIZE_BUCKETS);
    private long clusterPasses;
    private long clusterPassDurationUs;
    private long clusterReassignments;
    private long natsPublished;
    private long natsPublishFailed;
    private long natsDropped;
    private long natsSuperseded;
    private long natsReconnects;
    private int natsConnected;

    public MeterListenerMetricsCollector(
        MessagePipe messagePipe,
        ClientMessageCounters incomingMessageCounters,
        ServerMessageCounters outgoingMessageCounters)
    {
        this.messagePipe = messagePipe;
        this.incomingMessageCounters = incomingMessageCounters;
        this.outgoingMessageCounters = outgoingMessageCounters;

        listener = new MeterListener();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PulseMetrics.METER.Name)
                meterListener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        listener.SetMeasurementEventCallback<int>(OnIntMeasurement);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        listener.Start();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Reads current accumulated values and returns a snapshot.
    /// </summary>
    public MetricsSnapshot TakeSnapshot()
    {
        var byTransport = new MetricsSnapshot.PerTransportCounters[TRANSPORT_COUNT];

        for (var i = 0; i < TRANSPORT_COUNT; i++)
            byTransport[i] = new MetricsSnapshot.PerTransportCounters
            {
                TotalPeersConnected = Interlocked.Read(ref peersConnected[i]),
                TotalPeersDisconnected = Interlocked.Read(ref peersDisconnected[i]),
                ActivePeers = Volatile.Read(ref activePeers[i]),
                TotalBytesReceived = Interlocked.Read(ref bytesReceived[i]),
                TotalBytesSent = Interlocked.Read(ref bytesSent[i]),
                TotalPacketsReceived = Interlocked.Read(ref packetsReceived[i]),
                TotalPacketsSent = Interlocked.Read(ref packetsSent[i]),
                TotalUnauthMessagesSkipped = Interlocked.Read(ref unauthMessagesSkipped[i]),
                TotalSendFailures = Interlocked.Read(ref sendFailures[i]),
            };

        return new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot
            {
                ByTransport = byTransport,
                IncomingQueueDepth = messagePipe.IncomingQueueDepth,
                OutgoingQueueDepth = messagePipe.OutgoingQueueDepth,
                OutgoingDrainCycleUs = outgoingDrainCycleUs.Snapshot(),
                PeerRttMs = SnapshotPeerRtt(),
            },
            WebTransport = new MetricsSnapshot.WebTransportSnapshot
            {
                TotalDatagramsDroppedStale = Interlocked.Read(ref datagramsDroppedStale),
                TotalDatagramsDroppedOversize = Interlocked.Read(ref datagramsDroppedOversize),
            },
            Hardening = new MetricsSnapshot.HardeningSnapshot
            {
                TotalPreAuthIpLimitRefused = Interlocked.Read(ref preAuthIpLimitRefused),
                TotalPreAuthRefused = Interlocked.Read(ref preAuthRefused),
                TotalHandshakeAttemptsExceeded = Interlocked.Read(ref handshakeAttemptsExceeded),
                PreAuthInFlight = Volatile.Read(ref preAuthInFlight),
                TotalInputRateThrottled = Interlocked.Read(ref inputRateThrottled),
                TotalDiscreteEventThrottled = Interlocked.Read(ref discreteEventThrottled),
                TotalFieldValidationFailed = Interlocked.Read(ref fieldValidationFailed),
                TotalHandshakeReplayRejected = Interlocked.Read(ref handshakeReplayRejected),
                TotalBannedRefused = Interlocked.Read(ref bannedRefused),
                TotalCorruptedPacket = Interlocked.Read(ref corruptedPacket),
                IpLimitRefusedByClass = SnapshotIpLimitRefused(),
                TotalIpLimitWhitelistBypass = Interlocked.Read(ref ipLimitWhitelistBypass),
                IpLimitTrackedIps = Volatile.Read(ref ipLimitTrackedIps),
            },
            SceneListener = new MetricsSnapshot.SceneListenerSnapshot
            {
                Connected = Volatile.Read(ref sceneListenersConnected),
                TotalForbiddenMessagesDropped = Interlocked.Read(ref sceneListenerForbiddenMessagesDropped),
                VisibleSubjectsSum = Interlocked.Read(ref sceneListenerVisibleSubjectsSum),
                VisibleSubjectsCount = Interlocked.Read(ref sceneListenerVisibleSubjectsCount),
            },
            Simulation = new MetricsSnapshot.SimulationSnapshot
            {
                DeltaStalenessTier0Ms = deltaStalenessTier0.Snapshot(),
                DeltaStalenessTier1Ms = deltaStalenessTier1.Snapshot(),
                DeltaStalenessTier2Ms = deltaStalenessTier2.Snapshot(),
                TickDurationUs = tickDurationUs.Snapshot(),
                TotalTickOverruns = Interlocked.Read(ref tickOverruns),
            },
            Clusters = new MetricsSnapshot.ClustersSnapshot
            {
                ClusterCount = Volatile.Read(ref clusterCount),
                ClusterPeers = Volatile.Read(ref clusterPeers),
                ClusterSizeMax = Volatile.Read(ref clusterSizeMax),
                ClusterSize = clusterSize.Snapshot(),
                TotalPasses = Interlocked.Read(ref clusterPasses),
                TotalPassDurationUs = Interlocked.Read(ref clusterPassDurationUs),
                TotalReassignments = Interlocked.Read(ref clusterReassignments),
                TotalNatsPublished = Interlocked.Read(ref natsPublished),
                TotalNatsPublishFailed = Interlocked.Read(ref natsPublishFailed),
                TotalNatsDropped = Interlocked.Read(ref natsDropped),
                TotalNatsSuperseded = Interlocked.Read(ref natsSuperseded),
                TotalNatsReconnects = Interlocked.Read(ref natsReconnects),
                NatsConnected = Volatile.Read(ref natsConnected),
            },
            IncomingMessages = incomingMessageCounters,
            OutgoingMessages = outgoingMessageCounters,
        };
    }

    private HistogramSnapshot[] SnapshotPeerRtt()
    {
        var snapshots = new HistogramSnapshot[peerRttMs.Length];

        for (var i = 0; i < peerRttMs.Length; i++)
            snapshots[i] = peerRttMs[i].Snapshot();

        return snapshots;
    }

    private long[] SnapshotIpLimitRefused()
    {
        var byClass = new long[ipLimitRefused.Length];

        for (var i = 0; i < byClass.Length; i++)
            byClass[i] = Interlocked.Read(ref ipLimitRefused[i]);

        return byClass;
    }

    private void OnLongMeasurement(
        Instrument instrument, long value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        // Each case's string must match the instrument name declared in PulseMetrics.*; there is no
        // compile-time link, so a new instrument without a matching case here is silently dropped.
        switch (instrument.Name)
        {
            case "pulse.transport.peers_connected":
                Interlocked.Add(ref peersConnected[TransportIndex(tags)], value);
                break;
            case "pulse.transport.peers_disconnected":
                Interlocked.Add(ref peersDisconnected[TransportIndex(tags)], value);
                break;
            case "pulse.transport.bytes_received":
                Interlocked.Add(ref bytesReceived[TransportIndex(tags)], value);
                break;
            case "pulse.transport.bytes_sent":
                Interlocked.Add(ref bytesSent[TransportIndex(tags)], value);
                break;
            case "pulse.transport.packets_received":
                Interlocked.Add(ref packetsReceived[TransportIndex(tags)], value);
                break;
            case "pulse.transport.packets_sent":
                Interlocked.Add(ref packetsSent[TransportIndex(tags)], value);
                break;
            case "pulse.transport.unauth_messages_skipped":
                Interlocked.Add(ref unauthMessagesSkipped[TransportIndex(tags)], value);
                break;
            case "pulse.transport.send_failures":
                Interlocked.Add(ref sendFailures[TransportIndex(tags)], value);
                break;
            case "pulse.webtransport.datagrams_dropped_stale":
                Interlocked.Add(ref datagramsDroppedStale, value);
                break;
            case "pulse.webtransport.datagrams_dropped_oversize":
                Interlocked.Add(ref datagramsDroppedOversize, value);
                break;
            case "pulse.hardening.pre_auth_ip_limit_refused":
                Interlocked.Add(ref preAuthIpLimitRefused, value);
                break;
            case "pulse.hardening.pre_auth_refused":
                Interlocked.Add(ref preAuthRefused, value);
                break;
            case "pulse.hardening.handshake_attempts_exceeded":
                Interlocked.Add(ref handshakeAttemptsExceeded, value);
                break;
            case "pulse.hardening.input_rate_throttled":
                Interlocked.Add(ref inputRateThrottled, value);
                break;
            case "pulse.hardening.discrete_event_throttled":
                Interlocked.Add(ref discreteEventThrottled, value);
                break;
            case "pulse.hardening.field_validation_failed":
                Interlocked.Add(ref fieldValidationFailed, value);
                break;
            case "pulse.hardening.handshake_replay_rejected":
                Interlocked.Add(ref handshakeReplayRejected, value);
                break;
            case "pulse.hardening.banned_refused":
                Interlocked.Add(ref bannedRefused, value);
                break;
            case "pulse.hardening.corrupted_packet":
                Interlocked.Add(ref corruptedPacket, value);
                break;
            case "pulse.clusters.passes":
                Interlocked.Add(ref clusterPasses, value);
                break;
            case "pulse.clusters.pass_duration_us":
                Interlocked.Add(ref clusterPassDurationUs, value);
                break;
            case "pulse.clusters.reassignments":
                Interlocked.Add(ref clusterReassignments, value);
                break;
            case "pulse.nats.published":
                Interlocked.Add(ref natsPublished, value);
                break;
            case "pulse.nats.publish_failed":
                Interlocked.Add(ref natsPublishFailed, value);
                break;
            case "pulse.nats.dropped":
                Interlocked.Add(ref natsDropped, value);
                break;
            case "pulse.nats.superseded":
                Interlocked.Add(ref natsSuperseded, value);
                break;
            case "pulse.nats.reconnects":
                Interlocked.Add(ref natsReconnects, value);
                break;
            case "pulse.hardening.ip_limit_refused":
                Interlocked.Add(ref ipLimitRefused[ConnectionClassIndex(tags)], value);
                break;
            case "pulse.hardening.ip_limit_whitelist_bypass":
                Interlocked.Add(ref ipLimitWhitelistBypass, value);
                break;
            case "pulse.scene_listener.forbidden_messages_dropped":
                Interlocked.Add(ref sceneListenerForbiddenMessagesDropped, value);
                break;
            case "pulse.sim.delta_staleness_tier0_ms":
                deltaStalenessTier0.Record(value);
                break;
            case "pulse.sim.delta_staleness_tier1_ms":
                deltaStalenessTier1.Record(value);
                break;
            case "pulse.sim.delta_staleness_tier2_ms":
                deltaStalenessTier2.Record(value);
                break;
            case "pulse.sim.tick_duration_us":
                tickDurationUs.Record(value);
                break;
            case "pulse.sim.tick_overruns":
                Interlocked.Add(ref tickOverruns, value);
                break;
            case "pulse.transport.outgoing_drain_cycle_us":
                outgoingDrainCycleUs.Record(value);
                break;
            case "pulse.transport.peer_rtt_af_ms":
                peerRttMs[0].Record(value);
                break;
            case "pulse.transport.peer_rtt_as_ms":
                peerRttMs[1].Record(value);
                break;
            case "pulse.transport.peer_rtt_eu_ms":
                peerRttMs[2].Record(value);
                break;
            case "pulse.transport.peer_rtt_na_ms":
                peerRttMs[3].Record(value);
                break;
            case "pulse.transport.peer_rtt_oc_ms":
                peerRttMs[4].Record(value);
                break;
            case "pulse.transport.peer_rtt_sa_ms":
                peerRttMs[5].Record(value);
                break;
            case "pulse.transport.peer_rtt_unknown_ms":
                peerRttMs[6].Record(value);
                break;
        }
    }

    private void OnIntMeasurement(
        Instrument instrument, int value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case "pulse.transport.active_peers":
                Interlocked.Add(ref activePeers[TransportIndex(tags)], value);
                break;
            case "pulse.hardening.pre_auth_in_flight":
                Interlocked.Add(ref preAuthInFlight, value);
                break;
            case "pulse.clusters.count":
                Interlocked.Add(ref clusterCount, value);
                break;
            case "pulse.clusters.peers":
                Interlocked.Add(ref clusterPeers, value);
                break;
            case "pulse.clusters.size":
                clusterSize.Record(value);
                break;
            case "pulse.clusters.size_max":
                Interlocked.Add(ref clusterSizeMax, value);
                break;
            case "pulse.nats.connected":
                Interlocked.Add(ref natsConnected, value);
                break;
            case "pulse.hardening.ip_limit_tracked_ips":
                Interlocked.Add(ref ipLimitTrackedIps, value);
                break;
            case "pulse.scene_listener.connected":
                Interlocked.Add(ref sceneListenersConnected, value);
                break;
            case "pulse.scene_listener.visible_subjects":
                Interlocked.Add(ref sceneListenerVisibleSubjectsSum, value);
                Interlocked.Increment(ref sceneListenerVisibleSubjectsCount);
                break;
        }
    }

    /// <summary>
    ///     Resolves the transport bucket from a measurement's tags. Defaults to <see cref="TransportId.ENet" />
    ///     for an untagged transport measurement — every transport recording site tags itself, so the
    ///     default only guards against an accidentally-untagged site rather than mis-bucketing live traffic.
    /// </summary>
    private static int TransportIndex(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
            if (tag.Key == PulseMetrics.Transport.TRANSPORT_TAG_KEY && tag.Value is TransportId transport)
                return (int)transport;

        return (int)TransportId.ENet;
    }

    /// <summary>
    ///     Resolves the connection-class bucket from a measurement's tags. Defaults to
    ///     <see cref="ConnectionClass.PLAYER" /> for an untagged measurement — every recording site
    ///     tags itself, so the default only guards against an accidentally-untagged site.
    /// </summary>
    private static int ConnectionClassIndex(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
            if (tag.Key == PulseMetrics.Hardening.CONNECTION_CLASS_TAG_KEY && tag.Value is ConnectionClass connectionClass)
                return (int)connectionClass;

        return (int)ConnectionClass.PLAYER;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => listener.Dispose();
}
