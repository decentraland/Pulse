namespace Pulse.Metrics;

public readonly record struct MetricsSnapshot
{
    public TransportSnapshot Transport { get; init; }
    public WebTransportSnapshot WebTransport { get; init; }
    public HardeningSnapshot Hardening { get; init; }
    public ClustersSnapshot Clusters { get; init; }
    public ClientMessageCounters IncomingMessages { get; init; }
    public ServerMessageCounters OutgoingMessages { get; init; }

    public readonly record struct TransportSnapshot
    {
        /// <summary>Per-transport counters, indexed by <c>(int)TransportId</c>.</summary>
        public PerTransportCounters[] ByTransport { get; init; }

        // Shared pipeline queues — a single incoming channel and an aggregate outgoing depth across
        // both transports, so they are not attributable to one transport.
        public int IncomingQueueDepth { get; init; }
        public int OutgoingQueueDepth { get; init; }
    }

    public readonly record struct PerTransportCounters
    {
        public long TotalPeersConnected { get; init; }
        public long TotalPeersDisconnected { get; init; }
        public int ActivePeers { get; init; }
        public long TotalBytesReceived { get; init; }
        public long TotalBytesSent { get; init; }
        public long TotalPacketsReceived { get; init; }
        public long TotalPacketsSent { get; init; }
        public long TotalUnauthMessagesSkipped { get; init; }
        public long TotalSendFailures { get; init; }
    }

    public readonly record struct WebTransportSnapshot
    {
        public long TotalDatagramsDroppedStale { get; init; }
        public long TotalDatagramsDroppedOversize { get; init; }
    }

    /// <summary>
    ///     Cluster derivation and the outbound NATS feed. All zero while
    ///     <c>Clusters:Enabled</c> is false; NATS values also stay zero in stats-only mode.
    /// </summary>
    public readonly record struct ClustersSnapshot
    {
        public int ClusterCount { get; init; }

        /// <summary>Peers placed in a cluster; with <see cref="ClusterCount" /> gives the mean size.</summary>
        public int ClusterPeers { get; init; }

        /// <summary>Largest cluster; the histogram cannot recover it from its top bucket.</summary>
        public int ClusterSizeMax { get; init; }

        /// <summary>
        ///     Cluster-size histogram: per-bucket observation counts laid out by
        ///     <see cref="ClusterSizeHistogram" />, non-cumulative, plus the sum/count pair. Null when a
        ///     snapshot carries no histogram, which the exposition renders as an unobserved histogram
        ///     rather than omitting the series.
        /// </summary>
        public long[]? ClusterSizeBuckets { get; init; }
        public long ClusterSizeSum { get; init; }
        public long ClusterSizeCount { get; init; }

        public long TotalPasses { get; init; }
        public long TotalPassDurationUs { get; init; }
        public long TotalReassignments { get; init; }
        public long TotalNatsPublished { get; init; }
        public long TotalNatsPublishFailed { get; init; }
        public long TotalNatsDropped { get; init; }
        public long TotalNatsSuperseded { get; init; }
        public long TotalNatsReconnects { get; init; }
        public int NatsConnected { get; init; }
    }

    public readonly record struct HardeningSnapshot
    {
        public long TotalPreAuthIpLimitRefused { get; init; }
        public long TotalPreAuthRefused { get; init; }
        public long TotalHandshakeAttemptsExceeded { get; init; }
        public int PreAuthInFlight { get; init; }
        public long TotalInputRateThrottled { get; init; }
        public long TotalDiscreteEventThrottled { get; init; }
        public long TotalFieldValidationFailed { get; init; }
        public long TotalHandshakeReplayRejected { get; init; }
        public long TotalBannedRefused { get; init; }
        public long TotalCorruptedPacket { get; init; }
    }
}
