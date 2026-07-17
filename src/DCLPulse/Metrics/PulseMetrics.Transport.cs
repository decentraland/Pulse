using System.Diagnostics.Metrics;
using Pulse.Transport;

namespace Pulse.Metrics;

public static partial class PulseMetrics
{
    public static class Transport
    {
        /// <summary>Tag key for the <c>transport</c> dimension carried on the counters below.</summary>
        public const string TRANSPORT_TAG_KEY = "transport";

        // Cached per-transport tag, indexed by (int)TransportId, so the transport dimension can be
        // attached to a counter Add() without allocating on the hot path. The boxed TransportId value
        // is unboxed by MeterListenerMetricsCollector to bucket the measurement.
        private static readonly KeyValuePair<string, object?>[] TRANSPORT_TAGS = BuildTransportTags();

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

        /// <summary>The cached <c>transport</c> tag for <paramref name="transport" />, passed to a counter's <c>Add()</c>.</summary>
        public static KeyValuePair<string, object?> Tag(TransportId transport) =>
            TRANSPORT_TAGS[(int)transport];

        private static KeyValuePair<string, object?>[] BuildTransportTags()
        {
            TransportId[] values = Enum.GetValues<TransportId>();
            var tags = new KeyValuePair<string, object?>[values.Length];

            foreach (TransportId transport in values)
                tags[(int)transport] = new KeyValuePair<string, object?>(TRANSPORT_TAG_KEY, transport);

            return tags;
        }
    }
}
