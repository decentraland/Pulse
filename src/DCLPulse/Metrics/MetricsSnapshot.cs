using Decentraland.Pulse;

namespace Pulse.Metrics;

public readonly record struct MetricsSnapshot
{
    public TransportSnapshot Transport { get; init; }
    /// <summary>
    ///     Per-message-type incoming rates, keyed by <see cref="Decentraland.Pulse.ClientMessage.MessageOneofCase" />.
    /// </summary>
    public Dictionary<ClientMessage.MessageOneofCase, RateStats> IncomingMessages { get; init; }

    /// <summary>
    ///     Per-message-type outgoing rates, keyed by <see cref="Decentraland.Pulse.ServerMessage.MessageOneofCase" />.
    /// </summary>
    public Dictionary<ServerMessage.MessageOneofCase, RateStats> OutgoingMessages { get; init; }

    public readonly record struct TransportSnapshot
    {
        public long TotalPeersConnected { get; init; }
        public long TotalPeersDisconnected { get; init; }
        public int ActivePeers { get; init; }
        public long TotalBytesReceived { get; init; }
        public long TotalBytesSent { get; init; }
        public long TotalPacketsReceived { get; init; }
        public long TotalPacketsSent { get; init; }

        public RateStats BytesReceived { get; init; }
        public RateStats BytesSent { get; init; }
        public RateStats PacketsReceived { get; init; }
        public RateStats PacketsSent { get; init; }

        /// <summary>
        ///     Messages skipped because the peer was not yet authenticated.
        ///     Non-zero sustained values indicate clients sending data before handshake completes.
        /// </summary>
        public RateStats UnauthMessagesSkipped { get; init; }

        /// <summary>
        ///     Packets rejected by ENet's peer.Send() — internal queue full or throttled.
        ///     Non-zero means ENet is dropping outgoing packets before they reach the wire.
        /// </summary>
        public RateStats SendFailures { get; init; }

        /// <summary>
        ///     Pending messages in the incoming channel waiting for workers to process.
        ///     If this grows, workers cannot keep up with inbound message volume.
        /// </summary>
        public RateStats IncomingQueueDepth { get; init; }

        /// <summary>
        ///     Pending messages in the outgoing channel waiting for the ENet thread to flush.
        ///     If this grows, the ENet thread cannot keep up with outgoing message volume.
        /// </summary>
        public RateStats OutgoingQueueDepth { get; init; }
    }
}
