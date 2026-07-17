using System.Diagnostics.Metrics;
using Pulse.Messaging;
using Pulse.Transport;

namespace Pulse.Metrics;

/// <summary>
///     Subscribes to <see cref="PulseMetrics" /> instruments via <see cref="MeterListener" />
///     and accumulates raw counters. Consumers pull snapshots on demand via <see cref="TakeSnapshot" />.
///     Threading model:
///     - MeterListener callbacks fire on the recording thread (ENet / WebTransport / worker threads).
///       They do a single Interlocked.Add — minimal overhead on the hot path.
///     - <see cref="TakeSnapshot" /> is called by consumers on their own schedule.
///     Transport instruments carry a <c>transport</c> tag (see <see cref="PulseMetrics.Transport.Tag" />);
///     the callbacks bucket each measurement by <see cref="TransportId" /> read from that tag.
/// </summary>
public sealed class MeterListenerMetricsCollector : IMetricsCollector, IHostedService, IDisposable
{
    private static readonly int TRANSPORT_COUNT = Enum.GetValues<TransportId>().Length;

    private readonly MessagePipe messagePipe;
    private readonly ClientMessageCounters incomingMessageCounters;
    private readonly ServerMessageCounters outgoingMessageCounters;
    private readonly MeterListener listener;

    // Per-transport transport totals, indexed by (int)TransportId — written by MeterListener callbacks
    // on recording threads, one Interlocked.Add per measurement.
    private readonly long[] peersConnected = new long[TRANSPORT_COUNT];
    private readonly long[] peersDisconnected = new long[TRANSPORT_COUNT];
    private readonly int[] activePeers = new int[TRANSPORT_COUNT];
    private readonly long[] bytesReceived = new long[TRANSPORT_COUNT];
    private readonly long[] bytesSent = new long[TRANSPORT_COUNT];
    private readonly long[] packetsReceived = new long[TRANSPORT_COUNT];
    private readonly long[] packetsSent = new long[TRANSPORT_COUNT];
    private readonly long[] unauthMessagesSkipped = new long[TRANSPORT_COUNT];
    private readonly long[] sendFailures = new long[TRANSPORT_COUNT];

    // WebTransport-specific totals (no ENet analogue, so not per-transport).
    private long datagramsDroppedStale;
    private long datagramsDroppedOversize;

    // Hardening totals.
    private long preAuthIpLimitRefused;
    private long preAuthRefused;
    private long handshakeAttemptsExceeded;
    private int preAuthInFlight;
    private long inputRateThrottled;
    private long discreteEventThrottled;
    private long fieldValidationFailed;
    private long handshakeReplayRejected;
    private long bannedRefused;
    private long corruptedPacket;

    public MeterListenerMetricsCollector(
        MessagePipe messagePipe,
        ClientMessageCounters incomingMessageCounters,
        ServerMessageCounters outgoingMessageCounters)
    {
        this.messagePipe = messagePipe;
        this.incomingMessageCounters = incomingMessageCounters;
        this.outgoingMessageCounters = outgoingMessageCounters;

        listener = new MeterListener();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PulseMetrics.METER.Name)
                meterListener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        listener.SetMeasurementEventCallback<int>(OnIntMeasurement);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        listener.Start();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Reads current accumulated values and returns a snapshot.
    /// </summary>
    public MetricsSnapshot TakeSnapshot()
    {
        var byTransport = new MetricsSnapshot.PerTransportCounters[TRANSPORT_COUNT];

        for (var i = 0; i < TRANSPORT_COUNT; i++)
            byTransport[i] = new MetricsSnapshot.PerTransportCounters
            {
                TotalPeersConnected = Interlocked.Read(ref peersConnected[i]),
                TotalPeersDisconnected = Interlocked.Read(ref peersDisconnected[i]),
                ActivePeers = Volatile.Read(ref activePeers[i]),
                TotalBytesReceived = Interlocked.Read(ref bytesReceived[i]),
                TotalBytesSent = Interlocked.Read(ref bytesSent[i]),
                TotalPacketsReceived = Interlocked.Read(ref packetsReceived[i]),
                TotalPacketsSent = Interlocked.Read(ref packetsSent[i]),
                TotalUnauthMessagesSkipped = Interlocked.Read(ref unauthMessagesSkipped[i]),
                TotalSendFailures = Interlocked.Read(ref sendFailures[i]),
            };

        return new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot
            {
                ByTransport = byTransport,
                IncomingQueueDepth = messagePipe.IncomingQueueDepth,
                OutgoingQueueDepth = messagePipe.OutgoingQueueDepth,
            },
            WebTransport = new MetricsSnapshot.WebTransportSnapshot
            {
                TotalDatagramsDroppedStale = Interlocked.Read(ref datagramsDroppedStale),
                TotalDatagramsDroppedOversize = Interlocked.Read(ref datagramsDroppedOversize),
            },
            Hardening = new MetricsSnapshot.HardeningSnapshot
            {
                TotalPreAuthIpLimitRefused = Interlocked.Read(ref preAuthIpLimitRefused),
                TotalPreAuthRefused = Interlocked.Read(ref preAuthRefused),
                TotalHandshakeAttemptsExceeded = Interlocked.Read(ref handshakeAttemptsExceeded),
                PreAuthInFlight = Volatile.Read(ref preAuthInFlight),
                TotalInputRateThrottled = Interlocked.Read(ref inputRateThrottled),
                TotalDiscreteEventThrottled = Interlocked.Read(ref discreteEventThrottled),
                TotalFieldValidationFailed = Interlocked.Read(ref fieldValidationFailed),
                TotalHandshakeReplayRejected = Interlocked.Read(ref handshakeReplayRejected),
                TotalBannedRefused = Interlocked.Read(ref bannedRefused),
                TotalCorruptedPacket = Interlocked.Read(ref corruptedPacket),
            },
            IncomingMessages = incomingMessageCounters,
            OutgoingMessages = outgoingMessageCounters,
        };
    }

    private void OnLongMeasurement(
        Instrument instrument, long value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        // Each case's string must match the instrument name declared in PulseMetrics.*; there is no
        // compile-time link, so a new instrument without a matching case here is silently dropped.
        switch (instrument.Name)
        {
            case "pulse.transport.peers_connected":
                Interlocked.Add(ref peersConnected[TransportIndex(tags)], value);
                break;
            case "pulse.transport.peers_disconnected":
                Interlocked.Add(ref peersDisconnected[TransportIndex(tags)], value);
                break;
            case "pulse.transport.bytes_received":
                Interlocked.Add(ref bytesReceived[TransportIndex(tags)], value);
                break;
            case "pulse.transport.bytes_sent":
                Interlocked.Add(ref bytesSent[TransportIndex(tags)], value);
                break;
            case "pulse.transport.packets_received":
                Interlocked.Add(ref packetsReceived[TransportIndex(tags)], value);
                break;
            case "pulse.transport.packets_sent":
                Interlocked.Add(ref packetsSent[TransportIndex(tags)], value);
                break;
            case "pulse.transport.unauth_messages_skipped":
                Interlocked.Add(ref unauthMessagesSkipped[TransportIndex(tags)], value);
                break;
            case "pulse.transport.send_failures":
                Interlocked.Add(ref sendFailures[TransportIndex(tags)], value);
                break;
            case "pulse.webtransport.datagrams_dropped_stale":
                Interlocked.Add(ref datagramsDroppedStale, value);
                break;
            case "pulse.webtransport.datagrams_dropped_oversize":
                Interlocked.Add(ref datagramsDroppedOversize, value);
                break;
            case "pulse.hardening.pre_auth_ip_limit_refused":
                Interlocked.Add(ref preAuthIpLimitRefused, value);
                break;
            case "pulse.hardening.pre_auth_refused":
                Interlocked.Add(ref preAuthRefused, value);
                break;
            case "pulse.hardening.handshake_attempts_exceeded":
                Interlocked.Add(ref handshakeAttemptsExceeded, value);
                break;
            case "pulse.hardening.input_rate_throttled":
                Interlocked.Add(ref inputRateThrottled, value);
                break;
            case "pulse.hardening.discrete_event_throttled":
                Interlocked.Add(ref discreteEventThrottled, value);
                break;
            case "pulse.hardening.field_validation_failed":
                Interlocked.Add(ref fieldValidationFailed, value);
                break;
            case "pulse.hardening.handshake_replay_rejected":
                Interlocked.Add(ref handshakeReplayRejected, value);
                break;
            case "pulse.hardening.banned_refused":
                Interlocked.Add(ref bannedRefused, value);
                break;
            case "pulse.hardening.corrupted_packet":
                Interlocked.Add(ref corruptedPacket, value);
                break;
        }
    }

    private void OnIntMeasurement(
        Instrument instrument, int value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case "pulse.transport.active_peers":
                Interlocked.Add(ref activePeers[TransportIndex(tags)], value);
                break;
            case "pulse.hardening.pre_auth_in_flight":
                Interlocked.Add(ref preAuthInFlight, value);
                break;
        }
    }

    /// <summary>
    ///     Resolves the transport bucket from a measurement's tags. Defaults to <see cref="TransportId.ENet" />
    ///     for an untagged transport measurement — every transport recording site tags itself, so the
    ///     default only guards against an accidentally-untagged site rather than mis-bucketing live traffic.
    /// </summary>
    private static int TransportIndex(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
            if (tag.Key == PulseMetrics.Transport.TRANSPORT_TAG_KEY && tag.Value is TransportId transport)
                return (int)transport;

        return (int)TransportId.ENet;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => listener.Dispose();
}
