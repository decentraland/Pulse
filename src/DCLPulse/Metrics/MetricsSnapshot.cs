namespace Pulse.Metrics;

public readonly record struct MetricsSnapshot
{
    public TransportSnapshot Transport { get; init; }
    public WebTransportSnapshot WebTransport { get; init; }
    public HardeningSnapshot Hardening { get; init; }
    public SceneListenerSnapshot SceneListener { get; init; }
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

    public readonly record struct SceneListenerSnapshot
    {
        public int Connected { get; init; }
        public long TotalForbiddenMessagesDropped { get; init; }

        // Histogram summary for pulse.scene_listener.visible_subjects — running sum and
        // sample count. Consumers divide sum by count for the mean; Prometheus emits both
        // as the standard _sum / _count decomposition.
        public long VisibleSubjectsSum { get; init; }
        public long VisibleSubjectsCount { get; init; }
    }
}
