using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decentraland.Pulse;
using Pulse.Dashboard;
using Pulse.Messaging;

namespace Pulse.Metrics;

/// <summary>
///     Subscribes to <see cref="PulseMetrics" /> instruments via <see cref="MeterListener" />,
///     aggregates raw counters, computes rates, and periodically pushes a <see cref="MetricsSnapshot" />
///     to the registered <see cref="IDashboard" />.
///     Threading model:
///     - MeterListener callbacks fire on the recording thread (ENet / worker threads).
///     They do a single Interlocked.Add — minimal overhead on the hot path.
///     - A Timer fires every 500ms on a ThreadPool thread, reads accumulated totals,
///     computes per-second rates, and calls IDashboard.Update.
/// </summary>
public sealed class MetricsCollector : IHostedService, IDisposable
{
    private const int PERCENTILE_WINDOW = 120; // 60 seconds at 500ms

    private readonly IDashboard dashboard;
    private readonly MessagePipe messagePipe;
    private readonly MeterListener listener;
    private Timer? timer;

    // Accumulated totals — written by MeterListener callbacks on recording threads.
    private long peersConnected;
    private long peersDisconnected;
    private int activePeers;
    private long bytesReceived;
    private long bytesSent;
    private long packetsReceived;
    private long packetsSent;
    private long unauthMessagesSkipped;
    private long sendFailures;

    // Rate trackers — only touched by the timer callback.
    private readonly RateTracker bytesReceivedTracker = new (PERCENTILE_WINDOW);
    private readonly RateTracker bytesSentTracker = new (PERCENTILE_WINDOW);
    private readonly RateTracker packetsReceivedTracker = new (PERCENTILE_WINDOW);
    private readonly RateTracker packetsSentTracker = new (PERCENTILE_WINDOW);
    private readonly RateTracker unauthSkippedTracker = new (PERCENTILE_WINDOW);
    private readonly RateTracker sendFailuresTracker = new (PERCENTILE_WINDOW);
    private readonly GaugeTracker incomingQueueDepthTracker = new (PERCENTILE_WINDOW);
    private readonly GaugeTracker outgoingQueueDepthTracker = new (PERCENTILE_WINDOW);

    // Per-message-type rate trackers
    private readonly EnumRateTrackerGroup<ClientMessage.MessageOneofCase> incomingMessageTrackers;
    private readonly EnumRateTrackerGroup<ServerMessage.MessageOneofCase> outgoingMessageTrackers;

    private long prevTimestamp;

    public MetricsCollector(
        IDashboard dashboard,
        MessagePipe messagePipe,
        ClientMessageCounters incomingMessageCounters,
        ServerMessageCounters outgoingMessageCounters)
    {
        this.dashboard = dashboard;
        this.messagePipe = messagePipe;

        incomingMessageTrackers = new EnumRateTrackerGroup<ClientMessage.MessageOneofCase>(
            incomingMessageCounters, PERCENTILE_WINDOW,
            [
                ClientMessage.MessageOneofCase.Handshake,
                ClientMessage.MessageOneofCase.Input,
                ClientMessage.MessageOneofCase.Resync,
                ClientMessage.MessageOneofCase.ProfileAnnouncement,
                ClientMessage.MessageOneofCase.EmoteStart,
                ClientMessage.MessageOneofCase.EmoteStop,
                ClientMessage.MessageOneofCase.Teleport,
            ]);

        outgoingMessageTrackers = new EnumRateTrackerGroup<ServerMessage.MessageOneofCase>(
            outgoingMessageCounters, PERCENTILE_WINDOW,
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
            ]);

        listener = new MeterListener();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PulseMetrics.METER.Name)
                meterListener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        listener.SetMeasurementEventCallback<int>(OnIntMeasurement);
    }

    private void OnLongMeasurement(
        Instrument instrument, long value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        switch (instrument.Name)
        {
            case "pulse.transport.peers_connected":
                Interlocked.Add(ref peersConnected, value);
                break;
            case "pulse.transport.peers_disconnected":
                Interlocked.Add(ref peersDisconnected, value);
                break;
            case "pulse.transport.bytes_received":
                Interlocked.Add(ref bytesReceived, value);
                break;
            case "pulse.transport.bytes_sent":
                Interlocked.Add(ref bytesSent, value);
                break;
            case "pulse.transport.packets_received":
                Interlocked.Add(ref packetsReceived, value);
                break;
            case "pulse.transport.packets_sent":
                Interlocked.Add(ref packetsSent, value);
                break;
            case "pulse.transport.unauth_messages_skipped":
                Interlocked.Add(ref unauthMessagesSkipped, value);
                break;
            case "pulse.transport.send_failures":
                Interlocked.Add(ref sendFailures, value);
                break;
        }
    }

    private void OnIntMeasurement(
        Instrument instrument, int value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name == "pulse.transport.active_peers")
            Interlocked.Add(ref activePeers, value);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        prevTimestamp = Stopwatch.GetTimestamp();
        listener.Start();
        timer = new Timer(Tick, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        return Task.CompletedTask;
    }

    private void Tick(object? state)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = Stopwatch.GetElapsedTime(prevTimestamp, now).TotalSeconds;

        if (elapsedSec <= 0)
            return;

        prevTimestamp = now;

        int incomingDepth = messagePipe.IncomingQueueDepth;
        int outgoingDepth = messagePipe.OutgoingQueueDepth;

        var snapshot = new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot
            {
                TotalPeersConnected = Interlocked.Read(ref peersConnected),
                TotalPeersDisconnected = Interlocked.Read(ref peersDisconnected),
                ActivePeers = Volatile.Read(ref activePeers),
                TotalBytesReceived = Interlocked.Read(ref bytesReceived),
                TotalBytesSent = Interlocked.Read(ref bytesSent),
                TotalPacketsReceived = Interlocked.Read(ref packetsReceived),
                TotalPacketsSent = Interlocked.Read(ref packetsSent),
                BytesReceived = bytesReceivedTracker.Update(Interlocked.Read(ref bytesReceived), elapsedSec),
                BytesSent = bytesSentTracker.Update(Interlocked.Read(ref bytesSent), elapsedSec),
                PacketsReceived = packetsReceivedTracker.Update(Interlocked.Read(ref packetsReceived), elapsedSec),
                PacketsSent = packetsSentTracker.Update(Interlocked.Read(ref packetsSent), elapsedSec),
                UnauthMessagesSkipped = unauthSkippedTracker.Update(Interlocked.Read(ref unauthMessagesSkipped), elapsedSec),
                SendFailures = sendFailuresTracker.Update(Interlocked.Read(ref sendFailures), elapsedSec),
                IncomingQueueDepth = incomingQueueDepthTracker.Record(incomingDepth),
                OutgoingQueueDepth = outgoingQueueDepthTracker.Record(outgoingDepth),
            },
            IncomingMessages = incomingMessageTrackers.Update(elapsedSec),
            OutgoingMessages = outgoingMessageTrackers.Update(elapsedSec),
        };

        dashboard.Update(snapshot);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        timer?.Dispose();
        listener.Dispose();
    }

    /// <summary>
    ///     Groups <see cref="EnumCounters{TEnum}" /> with per-value <see cref="RateTracker" />s
    ///     and produces a snapshot dictionary in one call.
    /// </summary>
    private sealed class EnumRateTrackerGroup<TEnum>(
        EnumCounters<TEnum> counters,
        int percentileWindow,
        TEnum[] trackedValues) where TEnum: Enum
    {
        private readonly Dictionary<TEnum, RateTracker> trackers =
            trackedValues.ToDictionary(v => v, _ => new RateTracker(percentileWindow));

        public Dictionary<TEnum, RateStats> Update(double elapsedSec)
        {
            var result = new Dictionary<TEnum, RateStats>(trackers.Count);

            foreach ((TEnum value, RateTracker tracker) in trackers)
                result[value] = tracker.Update(counters.Read(value), elapsedSec);

            return result;
        }
    }
}
