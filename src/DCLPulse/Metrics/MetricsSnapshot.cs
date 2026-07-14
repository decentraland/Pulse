namespace Pulse.Metrics;

public readonly record struct MetricsSnapshot
{
    public TransportSnapshot Transport { get; init; }
    public HardeningSnapshot Hardening { get; init; }
    public SimulationSnapshot Simulation { get; init; }
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
        public HistogramSnapshot OutgoingDrainCycleUs { get; init; }

        /// <summary>Peer RTT histograms indexed by (int)Continent — see Continents.LABELS.</summary>
        public HistogramSnapshot[]? PeerRttMs { get; init; }
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

    public readonly record struct SimulationSnapshot
    {
        public HistogramSnapshot DeltaStalenessTier0Ms { get; init; }
        public HistogramSnapshot DeltaStalenessTier1Ms { get; init; }
        public HistogramSnapshot DeltaStalenessTier2Ms { get; init; }
        public HistogramSnapshot TickDurationUs { get; init; }
        public long TotalTickOverruns { get; init; }
    }
}
