namespace Pulse.Metrics;

public readonly record struct MetricsSnapshot
{
    public TransportSnapshot Transport { get; init; }
    public HardeningSnapshot Hardening { get; init; }
    public SceneListenerSnapshot SceneListener { get; init; }
    public ClientMessageCounters IncomingMessages { get; init; }
    public ServerMessageCounters OutgoingMessages { get; init; }

    public readonly record struct TransportSnapshot
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
        public int IncomingQueueDepth { get; init; }
        public int OutgoingQueueDepth { get; init; }
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