using System.Text;
using Decentraland.Pulse;

namespace Pulse.Metrics;

/// <summary>
///     Writes a <see cref="MetricsSnapshot" /> in Prometheus text exposition format.
/// </summary>
internal static class PrometheusFormatter
{
    private static readonly ClientMessage.MessageOneofCase[] INCOMING_MESSAGE_TYPES =
    [
        ClientMessage.MessageOneofCase.Handshake,
        ClientMessage.MessageOneofCase.Input,
        ClientMessage.MessageOneofCase.Resync,
        ClientMessage.MessageOneofCase.ProfileAnnouncement,
        ClientMessage.MessageOneofCase.EmoteStart,
        ClientMessage.MessageOneofCase.EmoteStop,
        ClientMessage.MessageOneofCase.Teleport,
    ];

    private static readonly ServerMessage.MessageOneofCase[] OUTGOING_MESSAGE_TYPES =
    [
        ServerMessage.MessageOneofCase.Handshake,
        ServerMessage.MessageOneofCase.PlayerStateFull,
        ServerMessage.MessageOneofCase.PlayerStateDelta,
        ServerMessage.MessageOneofCase.PlayerJoined,
        ServerMessage.MessageOneofCase.PlayerLeft,
        ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced,
        ServerMessage.MessageOneofCase.EmoteStarted,
        ServerMessage.MessageOneofCase.EmoteStopped,
        ServerMessage.MessageOneofCase.Teleported,
    ];
    public static void Write(StreamWriter writer, MetricsSnapshot snap)
    {
        WriteCounter(writer, "dcl_pulse_peers_connected_total", "Total peer connections since startup", snap.Transport.TotalPeersConnected);
        WriteCounter(writer, "dcl_pulse_peers_disconnected_total", "Total peer disconnections since startup", snap.Transport.TotalPeersDisconnected);
        WriteGauge(writer, "dcl_pulse_active_peers", "Currently connected peers", snap.Transport.ActivePeers);
        WriteCounter(writer, "dcl_pulse_bytes_received_total", "Total bytes received", snap.Transport.TotalBytesReceived);
        WriteCounter(writer, "dcl_pulse_bytes_sent_total", "Total bytes sent", snap.Transport.TotalBytesSent);
        WriteCounter(writer, "dcl_pulse_packets_received_total", "Total packets received", snap.Transport.TotalPacketsReceived);
        WriteCounter(writer, "dcl_pulse_packets_sent_total", "Total packets sent", snap.Transport.TotalPacketsSent);
        WriteCounter(writer, "dcl_pulse_unauth_messages_skipped_total", "Messages skipped from unauthenticated peers", snap.Transport.TotalUnauthMessagesSkipped);
        WriteCounter(writer, "dcl_pulse_send_failures_total", "Packets rejected by ENet send", snap.Transport.TotalSendFailures);
        WriteGauge(writer, "dcl_pulse_incoming_queue_depth", "Pending messages in incoming channel", snap.Transport.IncomingQueueDepth);
        WriteGauge(writer, "dcl_pulse_outgoing_queue_depth", "Pending messages in outgoing channel", snap.Transport.OutgoingQueueDepth);

        WriteEnumCounters(writer, "dcl_pulse_incoming_messages_total", "Total incoming messages by type",
            snap.IncomingMessages, INCOMING_MESSAGE_TYPES);
        WriteEnumCounters(writer, "dcl_pulse_outgoing_messages_total", "Total outgoing messages by type",
            snap.OutgoingMessages, OUTGOING_MESSAGE_TYPES);
    }

    private static void WriteCounter(StreamWriter writer, string name, string help, long value)
    {
        writer.Write("# HELP ");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(help);
        writer.Write("# TYPE ");
        writer.Write(name);
        writer.WriteLine(" counter");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(value);
    }

    private static void WriteGauge(StreamWriter writer, string name, string help, long value)
    {
        writer.Write("# HELP ");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(help);
        writer.Write("# TYPE ");
        writer.Write(name);
        writer.WriteLine(" gauge");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(value);
    }

    private static void WriteEnumCounters<TEnum>(
        StreamWriter writer, string name, string help,
        EnumCounters<TEnum> counters, TEnum[] values) where TEnum : Enum
    {
        writer.Write("# HELP ");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(help);
        writer.Write("# TYPE ");
        writer.Write(name);
        writer.WriteLine(" counter");

        foreach (TEnum type in values)
        {
            writer.Write(name);
            writer.Write("{type=\"");
            writer.Write(ToSnakeCase(type.ToString()));
            writer.Write("\"} ");
            writer.WriteLine(counters.Read(type));
        }
    }

    private static string ToSnakeCase(string pascalCase)
    {
        var sb = new StringBuilder(pascalCase.Length + 4);

        for (int i = 0; i < pascalCase.Length; i++)
        {
            char c = pascalCase[i];

            if (char.IsUpper(c) && i > 0)
                sb.Append('_');

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}