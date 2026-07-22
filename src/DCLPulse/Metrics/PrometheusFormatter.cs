using System.Diagnostics;
using System.Text;
using Decentraland.Pulse;
using Pulse.Transport;

namespace Pulse.Metrics;

/// <summary>
///     Writes a <see cref="MetricsSnapshot" /> in Prometheus text exposition format.
/// </summary>
internal static class PrometheusFormatter
{
    private static readonly TransportId[] TRANSPORTS = Enum.GetValues<TransportId>();

    private static readonly ClientMessage.MessageOneofCase[] INCOMING_MESSAGE_TYPES =
    [
        ClientMessage.MessageOneofCase.Handshake,
        ClientMessage.MessageOneofCase.Input,
        ClientMessage.MessageOneofCase.Resync,
        ClientMessage.MessageOneofCase.ProfileAnnouncement,
        ClientMessage.MessageOneofCase.EmoteStart,
        ClientMessage.MessageOneofCase.EmoteStop,
        ClientMessage.MessageOneofCase.Teleport,
        ClientMessage.MessageOneofCase.SceneListenerHandshake,
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
        MetricsSnapshot.PerTransportCounters[] byTransport = snap.Transport.ByTransport;
        WriteTransportCounter(writer, "dcl_pulse_peers_connected_total", "Total peer connections since startup", byTransport, static c => c.TotalPeersConnected);
        WriteTransportCounter(writer, "dcl_pulse_peers_disconnected_total", "Total peer disconnections since startup", byTransport, static c => c.TotalPeersDisconnected);
        WriteTransportGauge(writer, "dcl_pulse_active_peers", "Currently connected peers", byTransport, static c => c.ActivePeers);
        WriteTransportCounter(writer, "dcl_pulse_bytes_received_total", "Total bytes received", byTransport, static c => c.TotalBytesReceived);
        WriteTransportCounter(writer, "dcl_pulse_bytes_sent_total", "Total bytes sent", byTransport, static c => c.TotalBytesSent);
        WriteTransportCounter(writer, "dcl_pulse_packets_received_total", "Total packets received", byTransport, static c => c.TotalPacketsReceived);
        WriteTransportCounter(writer, "dcl_pulse_packets_sent_total", "Total packets sent", byTransport, static c => c.TotalPacketsSent);
        WriteTransportCounter(writer, "dcl_pulse_unauth_messages_skipped_total", "Messages skipped from unauthenticated peers", byTransport, static c => c.TotalUnauthMessagesSkipped);
        WriteTransportCounter(writer, "dcl_pulse_send_failures_total", "Packets rejected by the transport send", byTransport, static c => c.TotalSendFailures);
        WriteGauge(writer, "dcl_pulse_incoming_queue_depth", "Pending messages in incoming channel", snap.Transport.IncomingQueueDepth);
        WriteGauge(writer, "dcl_pulse_outgoing_queue_depth", "Pending messages across the outgoing channels", snap.Transport.OutgoingQueueDepth);

        WriteCounter(writer, "dcl_pulse_wt_datagrams_dropped_stale_total", "WebTransport datagrams dropped as stale/duplicate on the sequenced channel", snap.WebTransport.TotalDatagramsDroppedStale);
        WriteCounter(writer, "dcl_pulse_wt_datagrams_dropped_oversize_total", "WebTransport unreliable messages dropped for exceeding the datagram size cap", snap.WebTransport.TotalDatagramsDroppedOversize);

        WriteCounter(writer, "dcl_pulse_pre_auth_ip_limit_refused_total", "Connections refused by the per-IP pre-auth cap", snap.Hardening.TotalPreAuthIpLimitRefused);
        WriteCounter(writer, "dcl_pulse_pre_auth_refused_total", "Connections refused by the global pre-auth budget", snap.Hardening.TotalPreAuthRefused);
        WriteCounter(writer, "dcl_pulse_handshake_attempts_exceeded_total", "Peers disconnected after exceeding the handshake attempt limit", snap.Hardening.TotalHandshakeAttemptsExceeded);
        WriteGauge(writer, "dcl_pulse_pre_auth_in_flight", "Current number of peers in PENDING_AUTH", snap.Hardening.PreAuthInFlight);
        WriteCounter(writer, "dcl_pulse_input_rate_throttled_total", "PlayerStateInput messages dropped for exceeding MaxHz", snap.Hardening.TotalInputRateThrottled);
        WriteCounter(writer, "dcl_pulse_discrete_event_throttled_total", "Discrete events (emote start/stop, teleport) dropped by the token bucket", snap.Hardening.TotalDiscreteEventThrottled);
        WriteCounter(writer, "dcl_pulse_field_validation_failed_total", "Post-auth messages rejected for invalid fields (oversized strings, out-of-range indices, excessive durations)", snap.Hardening.TotalFieldValidationFailed);
        WriteCounter(writer, "dcl_pulse_handshake_replay_rejected_total", "Handshakes rejected because the (wallet, timestamp) pair was already accepted within the anti-replay window", snap.Hardening.TotalHandshakeReplayRejected);
        WriteCounter(writer, "dcl_pulse_banned_refused_total", "Handshake rejections and active-peer evictions triggered by the platform ban list", snap.Hardening.TotalBannedRefused);
        WriteCounter(writer, "dcl_pulse_corrupted_packet_total", "Corrupted packets observed per peer (oversized + protobuf parse failures). Sustained rate above the per-peer cap triggers PACKET_CORRUPTED disconnect.", snap.Hardening.TotalCorruptedPacket);

        WriteGauge(writer, "dcl_pulse_scene_listener_connected", "Currently connected scene listeners", snap.SceneListener.Connected);
        WriteCounter(writer, "dcl_pulse_scene_listener_forbidden_messages_dropped_total", "Messages dropped from scene listeners that attempted a forbidden operation", snap.SceneListener.TotalForbiddenMessagesDropped);
        WriteCounter(writer, "dcl_pulse_scene_listener_visible_subjects_sum", "Sum of visible subjects observed across scene-listener announcements (histogram _sum)", snap.SceneListener.VisibleSubjectsSum);
        WriteCounter(writer, "dcl_pulse_scene_listener_visible_subjects_count", "Number of scene-listener visible-subject observations (histogram _count)", snap.SceneListener.VisibleSubjectsCount);

        WriteEnumCounters(writer, "dcl_pulse_incoming_messages_total", "Total incoming messages by type",
            snap.IncomingMessages, INCOMING_MESSAGE_TYPES);
        WriteEnumCounters(writer, "dcl_pulse_outgoing_messages_total", "Total outgoing messages by type",
            snap.OutgoingMessages, OUTGOING_MESSAGE_TYPES);

        WriteProcessMetrics(writer);
    }

    private static void WriteProcessMetrics(StreamWriter writer)
    {
        using var process = Process.GetCurrentProcess();

        // GC
        WriteGauge(writer, "dotnet_gc_collections_total", "Total GC collections by generation",
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        WriteGauge(writer, "dotnet_gc_heap_size_bytes", "GC heap size in bytes", GC.GetTotalMemory(false));
        WriteGauge(writer, "dotnet_gc_allocated_bytes_total", "Total bytes allocated over lifetime", GC.GetTotalAllocatedBytes());

        // Memory
        WriteGauge(writer, "process_working_set_bytes", "Process working set in bytes", process.WorkingSet64);
        WriteGauge(writer, "process_private_memory_bytes", "Process private memory in bytes", process.PrivateMemorySize64);

        // Thread pool
        ThreadPool.GetAvailableThreads(out int workerAvailable, out int ioAvailable);
        ThreadPool.GetMaxThreads(out int workerMax, out int ioMax);
        WriteGauge(writer, "dotnet_threadpool_worker_active", "Active thread pool worker threads", workerMax - workerAvailable);
        WriteGauge(writer, "dotnet_threadpool_io_active", "Active thread pool IO threads", ioMax - ioAvailable);

        // Uptime
        WriteGauge(writer, "process_uptime_seconds", "Process uptime in seconds",
            (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds);

        // CPU
        WriteGauge(writer, "process_cpu_seconds_total", "Total CPU time in seconds",
            (long)process.TotalProcessorTime.TotalSeconds);
    }

    private static void WriteGauge(StreamWriter writer, string name, string help,
        long gen0, long gen1, long gen2)
    {
        writer.Write("# HELP ");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(help);
        writer.Write("# TYPE ");
        writer.Write(name);
        writer.WriteLine(" gauge");
        writer.Write(name);
        writer.WriteLine("{generation=\"gen0\"} " + gen0);
        writer.Write(name);
        writer.WriteLine("{generation=\"gen1\"} " + gen1);
        writer.Write(name);
        writer.WriteLine("{generation=\"gen2\"} " + gen2);
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

    private static void WriteTransportCounter(
        StreamWriter writer, string name, string help,
        MetricsSnapshot.PerTransportCounters[] byTransport,
        Func<MetricsSnapshot.PerTransportCounters, long> selector) =>
        WriteTransportSeries(writer, name, help, "counter", byTransport, selector);

    private static void WriteTransportGauge(
        StreamWriter writer, string name, string help,
        MetricsSnapshot.PerTransportCounters[] byTransport,
        Func<MetricsSnapshot.PerTransportCounters, long> selector) =>
        WriteTransportSeries(writer, name, help, "gauge", byTransport, selector);

    private static void WriteTransportSeries(
        StreamWriter writer, string name, string help, string type,
        MetricsSnapshot.PerTransportCounters[] byTransport,
        Func<MetricsSnapshot.PerTransportCounters, long> selector)
    {
        writer.Write("# HELP ");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(help);
        writer.Write("# TYPE ");
        writer.Write(name);
        writer.Write(' ');
        writer.WriteLine(type);

        foreach (TransportId transport in TRANSPORTS)
        {
            writer.Write(name);
            writer.Write("{transport=\"");
            writer.Write(transport.ToString().ToLowerInvariant());
            writer.Write("\"} ");
            writer.WriteLine(selector(byTransport[(int)transport]));
        }
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
