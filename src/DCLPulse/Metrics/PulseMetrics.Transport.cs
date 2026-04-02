using System.Diagnostics.Metrics;

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
    }
}
