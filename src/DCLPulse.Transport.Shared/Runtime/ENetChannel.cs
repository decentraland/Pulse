using ENet;

namespace Pulse.Transport
{
    /// <summary>
    ///     RELIABLE and UNRELIABLE_SEQUENCED share the same ENet channel (ch0). This simplifies
    ///     the channel layout but introduces two ENet-level side effects:
    ///     <para />
    ///     <b>1. Head-of-line blocking.</b> If a reliable packet is in-flight and unacknowledged,
    ///     ENet holds back all subsequent sequenced-unreliable packets on that channel until the
    ///     reliable one is ACK'd. In practice this means STATE_DELTA delivery can stall while a
    ///     lost reliable message (snapshot, emote, resync) is retransmitted. The stall is bounded
    ///     by one RTT and reliable messages are infrequent relative to the tick rate, so the
    ///     impact is small.
    ///     <para />
    ///     <b>2. Sequence counter reset.</b> Each reliable send resets the channel's
    ///     <c>outgoingUnreliableSequenceNumber</c> to 0. On the receive side the counter is
    ///     likewise reset when a reliable packet is dispatched. This partitions unreliable
    ///     ordering into discrete windows bounded by reliable packets — ordering is maintained
    ///     within each window but not across them. This is harmless because STATE_DELTA carries
    ///     its own application-level sequence number for gap detection.
    ///     <para />
    ///     Unsequenced packets (UNRELIABLE_UNSEQUENCED, ch1) are immune to both effects — they
    ///     bypass sequence tracking entirely.
    /// </summary>
    public readonly struct ENetChannel
    {
        public readonly byte ChannelId;
        public readonly PacketFlags PacketMode;

        // ReSharper disable once ConvertToPrimaryConstructor
        // ReSharper disable once MemberCanBePrivate.Global
        public ENetChannel(byte channelId, PacketFlags packetMode)
        {
            ChannelId = channelId;
            PacketMode = packetMode;
        }

        public const int COUNT = 2;

        public static readonly ENetChannel RELIABLE = new (0, PacketFlags.Reliable);

        /// <summary>
        ///     Unreliable sequenced channel used for high-frequency STATE_DELTA fan-out.
        ///     <para />
        ///     The <see cref="PacketFlags.Unthrottled" /> flag disables ENet's send-side throttle
        ///     (<c>packetThrottleCounter</c> check in <c>enet_protocol_send_outgoing_commands</c>).
        ///     Without it, ENet silently destroys unreliable packets before they reach the wire
        ///     when <c>packetThrottle</c> drops below 32 — which happens when measured RTT exceeds
        ///     <c>lastRTT + 40 ms + 2 * variance</c>. With many peers, even localhost can trigger
        ///     this if the service loop stalls, and recovery is slow (+2 per 5-second interval).
        ///     <para />
        ///     Dropping a STATE_DELTA is strictly worse than sending it: the client detects a
        ///     sequence gap and issues a RESYNC_REQUEST, which costs a full reliable round-trip.
        ///     Application-level rate control (tier divisors) already governs send frequency,
        ///     making the ENet throttle redundant for this channel.
        /// </summary>
        public static readonly ENetChannel UNRELIABLE_SEQUENCED = new (0, PacketFlags.Unthrottled);

        public static readonly ENetChannel UNRELIABLE_UNSEQUENCED = new (1, PacketFlags.Unsequenced);
    }
}
