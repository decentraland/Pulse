using ENet;

namespace Pulse.Transport
{
    /// <summary>
    ///     Reliable packets on a channel block sequenced unreliable packets on the same channel.
    ///     Here's what happens concretely:
    ///     Unreliable packets are still discarded if a newer sequence number has already been received — that's fine and expected.
    ///     But if a reliable packet is in-flight and unacknowledged, ENet will hold back subsequent sequenced unreliable packets on that same channel until the reliable one is ACK'd. This is head-of-line blocking — exactly what you're trying to avoid for position updates.
    ///     Unsequenced packets are immune to this — they bypass the sequence tracking entirely and will go through regardless of pending reliable packets on the channel.
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

        public const int COUNT = 3;

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
        public static readonly ENetChannel UNRELIABLE_SEQUENCED = new (1, PacketFlags.Unthrottled);

        public static readonly ENetChannel UNRELIABLE_UNSEQUENCED = new (2, PacketFlags.Unsequenced);
    }
}
