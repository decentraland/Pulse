using System.Diagnostics.Metrics;
using Pulse.Messaging;

namespace Pulse.Metrics;

/// <summary>
///     Subscribes to <see cref="PulseMetrics" /> instruments via <see cref="MeterListener" />
///     and accumulates raw counters. Consumers pull snapshots on demand via <see cref="TakeSnapshot" />.
///     Threading model:
///     - MeterListener callbacks fire on the recording thread (ENet / worker threads).
///       They do a single Interlocked.Add — minimal overhead on the hot path.
///     - <see cref="TakeSnapshot" /> is called by consumers on their own schedule.
/// </summary>
public sealed class MeterListenerMetricsCollector : IMetricsCollector, IHostedService, IDisposable
{
    private readonly MessagePipe messagePipe;
    private readonly ClientMessageCounters incomingMessageCounters;
    private readonly ServerMessageCounters outgoingMessageCounters;
    private readonly MeterListener listener;

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
        return new MetricsSnapshot
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
                TotalUnauthMessagesSkipped = Interlocked.Read(ref unauthMessagesSkipped),
                TotalSendFailures = Interlocked.Read(ref sendFailures),
                IncomingQueueDepth = messagePipe.IncomingQueueDepth,
                OutgoingQueueDepth = messagePipe.OutgoingQueueDepth,
            },
            IncomingMessages = incomingMessageCounters,
            OutgoingMessages = outgoingMessageCounters,
        };
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => listener.Dispose();
}