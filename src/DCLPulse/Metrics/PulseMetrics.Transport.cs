using System.Diagnostics.Metrics;
using Pulse.Transport.Geo;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static class Transport
    {
        public static readonly Counter<long> PEERS_CONNECTED =
            METER.CreateCounter<long>("pulse.transport.peers_connected");

        public static readonly Counter<long> PEERS_DISCONNECTED =
            METER.CreateCounter<long>("pulse.transport.peers_disconnected");

        public static readonly UpDownCounter<int> ACTIVE_PEERS =
            METER.CreateUpDownCounter<int>("pulse.transport.active_peers");

        public static readonly Counter<long> BYTES_RECEIVED =
            METER.CreateCounter<long>("pulse.transport.bytes_received");

        public static readonly Counter<long> BYTES_SENT =
            METER.CreateCounter<long>("pulse.transport.bytes_sent");

        public static readonly Counter<long> PACKETS_RECEIVED =
            METER.CreateCounter<long>("pulse.transport.packets_received");

        public static readonly Counter<long> PACKETS_SENT =
            METER.CreateCounter<long>("pulse.transport.packets_sent");

        public static readonly Counter<long> UNAUTH_MESSAGES_SKIPPED =
            METER.CreateCounter<long>("pulse.transport.unauth_messages_skipped");

        public static readonly Counter<long> SEND_FAILURES =
            METER.CreateCounter<long>("pulse.transport.send_failures");

        public static readonly Histogram<long> OUTGOING_DRAIN_CYCLE_US =
            METER.CreateHistogram<long>("pulse.transport.outgoing_drain_cycle_us");

        /// <summary>
        ///     Bucket upper bounds (ms) for peer RTT — spans LAN to intercontinental.
        /// </summary>
        public static readonly long[] RTT_BUCKETS_MS = [15, 30, 50, 75, 100, 150, 200, 300, 500, 1000];

        /// <summary>
        ///     ENet smoothed RoundTripTime sampled per connected peer every few seconds,
        ///     split by the peer's continent (resolved from its IP at connect).
        ///     Indexed by (int)Continent; labels in Continents.LABELS.
        /// </summary>
        public static readonly Histogram<long>[] PEER_RTT_MS = CreatePeerRttInstruments();

        private static Histogram<long>[] CreatePeerRttInstruments()
        {
            var instruments = new Histogram<long>[Continents.COUNT];

            for (var i = 0; i < instruments.Length; i++)
                instruments[i] = METER.CreateHistogram<long>($"pulse.transport.peer_rtt_{Continents.LABELS[i]}_ms");

            return instruments;
        }
    }
}
