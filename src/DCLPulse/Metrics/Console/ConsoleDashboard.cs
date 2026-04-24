using Decentraland.Pulse;
using System.Diagnostics;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Collections;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace Pulse.Metrics.Console;

/// <summary>
///     Fullscreen terminal dashboard powered by XenoAtom.Terminal.UI.
///     Runs <see cref="TerminalExtensions.Run" /> on a dedicated background thread.
///     Pulls raw <see cref="MetricsSnapshot" /> from <see cref="IMetricsCollector" /> every ~500ms,
///     computes rates and percentiles locally, and renders into reactive <see cref="State{T}" />
///     objects and sparkline buffers.
///     Logs are captured by <see cref="DashboardLoggerProvider" /> and displayed in an
///     embedded <see cref="LogControl" /> panel.
/// </summary>
public sealed class ConsoleDashboard(
    LogControl logControl,
    DashboardLoggerProvider loggerProvider,
    IMetricsCollector metricsCollector) : IHostedService
{
    private const int SPARKLINE_MAX_SAMPLES = 120; // 60 seconds at 500ms interval
    private const long SNAPSHOT_INTERVAL_MS = 500;

    // Fixed column widths — consistent across all tables.
    private const int COL_LABEL = 20;
    private const int COL_VALUE = 12;
    private const int COL_PERCENTILE = 12;

    // Color groups — inbound (receiving) vs outbound (sending) vs peers vs backpressure
    private static readonly Color COLOR_PEERS = Colors.MediumPurple;
    private static readonly Color COLOR_INBOUND = Colors.DodgerBlue;
    private static readonly Color COLOR_OUTBOUND = Colors.Coral;
    private static readonly Color COLOR_BACKPRESSURE = Colors.Yellow;
    private static readonly Color COLOR_ERROR = Colors.Red;

    private static readonly SparklineStyle STYLE_PEERS = SparklineStyle.Default with
    {
        Style = Style.None.WithForeground(COLOR_PEERS),
    };

    private static readonly SparklineStyle STYLE_INBOUND = SparklineStyle.Default with
    {
        Style = Style.None.WithForeground(COLOR_INBOUND),
    };

    private static readonly SparklineStyle STYLE_OUTBOUND = SparklineStyle.Default with
    {
        Style = Style.None.WithForeground(COLOR_OUTBOUND),
    };

    private static readonly SparklineStyle STYLE_BACKPRESSURE = SparklineStyle.Default with
    {
        Style = Style.None.WithForeground(COLOR_BACKPRESSURE),
    };

    private static readonly SparklineStyle STYLE_ERROR = SparklineStyle.Default with
    {
        Style = Style.None.WithForeground(COLOR_ERROR),
    };

    // Per-message-type display config — single source of truth for labels and colors.
    private static readonly MessageTableConfig<ClientMessage.MessageOneofCase> INCOMING_MESSAGES_CONFIG = new ("Incoming Messages",
    [
        (ClientMessage.MessageOneofCase.Handshake, "Handshake", STYLE_INBOUND),
        (ClientMessage.MessageOneofCase.Input, "Input", STYLE_INBOUND),
        (ClientMessage.MessageOneofCase.Resync, "Resync", STYLE_BACKPRESSURE),
        (ClientMessage.MessageOneofCase.ProfileAnnouncement, "ProfileAnnouncement", STYLE_INBOUND),
        (ClientMessage.MessageOneofCase.EmoteStart, "EmoteStart", STYLE_INBOUND),
        (ClientMessage.MessageOneofCase.EmoteStop, "EmoteStop", STYLE_INBOUND),
        (ClientMessage.MessageOneofCase.Teleport, "Teleport", STYLE_INBOUND),
    ]);

    private static readonly MessageTableConfig<ServerMessage.MessageOneofCase> OUTGOING_MESSAGES_CONFIG = new ("Outgoing Messages",
    [
        (ServerMessage.MessageOneofCase.Handshake, "Handshake", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.PlayerStateFull, "PlayerStateFull", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.PlayerStateDelta, "PlayerStateDelta", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.PlayerJoined, "PlayerJoined", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.PlayerLeft, "PlayerLeft", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced, "ProfileVersion", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.EmoteStarted, "EmoteStarted", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.EmoteStopped, "EmoteStopped", STYLE_OUTBOUND),
        (ServerMessage.MessageOneofCase.Teleported, "Teleported", STYLE_OUTBOUND),
    ]);

    // Rate trackers — compute rates and percentiles from raw totals.
    private readonly RateTracker bytesReceivedTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker bytesSentTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker packetsReceivedTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker packetsSentTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker unauthSkippedTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker sendFailuresTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly GaugeTracker incomingQueueDepthTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly GaugeTracker outgoingQueueDepthTracker = new (SPARKLINE_MAX_SAMPLES);

    // Hardening trackers.
    private readonly RateTracker preAuthIpLimitRefusedTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker preAuthRefusedTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker handshakeAttemptsExceededTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly GaugeTracker preAuthInFlightTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker inputRateThrottledTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker discreteEventThrottledTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker fieldValidationFailedTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker handshakeReplayRejectedTracker = new (SPARKLINE_MAX_SAMPLES);
    private readonly RateTracker bannedRefusedTracker = new (SPARKLINE_MAX_SAMPLES);

    // Per-message-type rate trackers
    private readonly Dictionary<ClientMessage.MessageOneofCase, RateTracker> incomingRateTrackers =
        INCOMING_MESSAGES_CONFIG.Entries.ToDictionary(e => e.Type, _ => new RateTracker(SPARKLINE_MAX_SAMPLES));

    private readonly Dictionary<ServerMessage.MessageOneofCase, RateTracker> outgoingRateTrackers =
        OUTGOING_MESSAGES_CONFIG.Entries.ToDictionary(e => e.Type, _ => new RateTracker(SPARKLINE_MAX_SAMPLES));

    // Pre-allocated rate dictionaries — reused every tick, only values change.
    private readonly Dictionary<ClientMessage.MessageOneofCase, RateStats> incomingRates =
        INCOMING_MESSAGES_CONFIG.Entries.ToDictionary(e => e.Type, _ => default(RateStats));

    private readonly Dictionary<ServerMessage.MessageOneofCase, RateStats> outgoingRates =
        OUTGOING_MESSAGES_CONFIG.Entries.ToDictionary(e => e.Type, _ => default(RateStats));

    // Reactive state — written on UI thread inside onUpdate callback.
    private readonly State<string> activePeers = new ("0");
    private readonly State<string> totalConnected = new ("0");
    private readonly State<string> totalDisconnected = new ("0");
    private readonly RateStatsView bytesIn = new ();
    private readonly RateStatsView bytesOut = new ();
    private readonly RateStatsView packetsIn = new ();
    private readonly RateStatsView packetsOut = new ();
    private readonly RateStatsView unauthSkipped = new ();
    private readonly RateStatsView sendFailures = new ();
    private readonly RateStatsView incomingQueue = new ();
    private readonly RateStatsView outgoingQueue = new ();
    private readonly RateStatsView preAuthIpLimitRefused = new ();
    private readonly RateStatsView preAuthRefused = new ();
    private readonly RateStatsView handshakeAttemptsExceeded = new ();
    private readonly RateStatsView preAuthInFlight = new ();
    private readonly RateStatsView inputRateThrottled = new ();
    private readonly RateStatsView discreteEventThrottled = new ();
    private readonly RateStatsView fieldValidationFailed = new ();
    private readonly RateStatsView handshakeReplayRejected = new ();
    private readonly RateStatsView bannedRefused = new ();

    // Per-message-type views
    private readonly MessageTableState<ClientMessage.MessageOneofCase> incomingMessagesState = new (INCOMING_MESSAGES_CONFIG);
    private readonly MessageTableState<ServerMessage.MessageOneofCase> outgoingMessagesState = new (OUTGOING_MESSAGES_CONFIG);

    // Charts — pre-filled to SPARKLINE_MAX_SAMPLES so the layout size is constant from the start.
    private readonly Sparkline activePeersChart = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline bytesInSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline bytesOutSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline packetsInSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline packetsOutSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline unauthSkippedSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline sendFailuresSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline incomingQueueSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline outgoingQueueSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline preAuthIpLimitRefusedSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline preAuthRefusedSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline handshakeAttemptsExceededSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline preAuthInFlightSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline inputRateThrottledSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline discreteEventThrottledSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline fieldValidationFailedSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline handshakeReplayRejectedSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));
    private readonly Sparkline bannedRefusedSparkline = new (Enumerable.Repeat(0.0, SPARKLINE_MAX_SAMPLES));

    private long lastSnapshotTimestamp = Stopwatch.GetTimestamp();

    private Thread? uiThread;
    private volatile bool stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {

        uiThread = new Thread(RunUi)
        {
            Name = "Dashboard",
            IsBackground = true,
        };

        uiThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stopping = true;
        uiThread?.Join(TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    private void RunUi()
    {
        Visual visual = BuildVisualTree();

        Terminal.Run(visual, () =>
        {
            if (stopping)
                return TerminalLoopResult.Stop;

            TryConsumeSnapshot();
            loggerProvider.DrainTo(logControl);
            return TerminalLoopResult.Continue;
        });
    }

    private void TryConsumeSnapshot()
    {
        long now = Stopwatch.GetTimestamp();

        if (Stopwatch.GetElapsedTime(lastSnapshotTimestamp, now).TotalMilliseconds < SNAPSHOT_INTERVAL_MS)
            return;

        double elapsed = Stopwatch.GetElapsedTime(lastSnapshotTimestamp, now).TotalSeconds;
        lastSnapshotTimestamp = now;

        if (elapsed <= 0)
            return;

        MetricsSnapshot snap = metricsCollector.TakeSnapshot();

        activePeers.Value = snap.Transport.ActivePeers.ToString("N0");
        totalConnected.Value = snap.Transport.TotalPeersConnected.ToString("N0");
        totalDisconnected.Value = snap.Transport.TotalPeersDisconnected.ToString("N0");

        RateStats bytesReceivedRate = bytesReceivedTracker.Update(snap.Transport.TotalBytesReceived, elapsed);
        RateStats bytesSentRate = bytesSentTracker.Update(snap.Transport.TotalBytesSent, elapsed);
        RateStats packetsReceivedRate = packetsReceivedTracker.Update(snap.Transport.TotalPacketsReceived, elapsed);
        RateStats packetsSentRate = packetsSentTracker.Update(snap.Transport.TotalPacketsSent, elapsed);
        RateStats unauthRate = unauthSkippedTracker.Update(snap.Transport.TotalUnauthMessagesSkipped, elapsed);
        RateStats failuresRate = sendFailuresTracker.Update(snap.Transport.TotalSendFailures, elapsed);
        RateStats inQueueRate = incomingQueueDepthTracker.Record(snap.Transport.IncomingQueueDepth);
        RateStats outQueueRate = outgoingQueueDepthTracker.Record(snap.Transport.OutgoingQueueDepth);

        bytesIn.Apply(bytesReceivedRate, ByteFormat.FormatRate);
        bytesOut.Apply(bytesSentRate, ByteFormat.FormatRate);
        packetsIn.Apply(packetsReceivedRate, v => v.ToString("N0"));
        packetsOut.Apply(packetsSentRate, v => v.ToString("N0"));
        unauthSkipped.Apply(unauthRate, v => v.ToString("N0"));
        sendFailures.Apply(failuresRate, v => v.ToString("N0"));
        incomingQueue.Apply(inQueueRate, v => v.ToString("N0"));
        outgoingQueue.Apply(outQueueRate, v => v.ToString("N0"));

        ShiftSample(activePeersChart.Values, snap.Transport.ActivePeers);
        ShiftSample(bytesInSparkline.Values, bytesReceivedRate.PerSec);
        ShiftSample(bytesOutSparkline.Values, bytesSentRate.PerSec);
        ShiftSample(packetsInSparkline.Values, packetsReceivedRate.PerSec);
        ShiftSample(packetsOutSparkline.Values, packetsSentRate.PerSec);
        ShiftSample(unauthSkippedSparkline.Values, unauthRate.PerSec);
        ShiftSample(sendFailuresSparkline.Values, failuresRate.PerSec);
        ShiftSample(incomingQueueSparkline.Values, snap.Transport.IncomingQueueDepth);
        ShiftSample(outgoingQueueSparkline.Values, snap.Transport.OutgoingQueueDepth);

        // Hardening rates + gauge.
        RateStats ipLimitRefusedRate = preAuthIpLimitRefusedTracker.Update(snap.Hardening.TotalPreAuthIpLimitRefused, elapsed);
        RateStats preAuthRefusedRate = preAuthRefusedTracker.Update(snap.Hardening.TotalPreAuthRefused, elapsed);
        RateStats handshakeExceededRate = handshakeAttemptsExceededTracker.Update(snap.Hardening.TotalHandshakeAttemptsExceeded, elapsed);
        RateStats preAuthInFlightStats = preAuthInFlightTracker.Record(snap.Hardening.PreAuthInFlight);

        preAuthIpLimitRefused.Apply(ipLimitRefusedRate, v => v.ToString("N0"));
        preAuthRefused.Apply(preAuthRefusedRate, v => v.ToString("N0"));
        handshakeAttemptsExceeded.Apply(handshakeExceededRate, v => v.ToString("N0"));
        preAuthInFlight.Apply(preAuthInFlightStats, v => v.ToString("N0"));

        ShiftSample(preAuthIpLimitRefusedSparkline.Values, ipLimitRefusedRate.PerSec);
        ShiftSample(preAuthRefusedSparkline.Values, preAuthRefusedRate.PerSec);
        ShiftSample(handshakeAttemptsExceededSparkline.Values, handshakeExceededRate.PerSec);
        ShiftSample(preAuthInFlightSparkline.Values, snap.Hardening.PreAuthInFlight);

        RateStats inputThrottledRate = inputRateThrottledTracker.Update(snap.Hardening.TotalInputRateThrottled, elapsed);
        RateStats discreteThrottledRate = discreteEventThrottledTracker.Update(snap.Hardening.TotalDiscreteEventThrottled, elapsed);
        RateStats fieldValidationRate = fieldValidationFailedTracker.Update(snap.Hardening.TotalFieldValidationFailed, elapsed);
        RateStats replayRejectedRate = handshakeReplayRejectedTracker.Update(snap.Hardening.TotalHandshakeReplayRejected, elapsed);
        RateStats bannedRefusedRate = bannedRefusedTracker.Update(snap.Hardening.TotalBannedRefused, elapsed);

        inputRateThrottled.Apply(inputThrottledRate, v => v.ToString("N0"));
        discreteEventThrottled.Apply(discreteThrottledRate, v => v.ToString("N0"));
        fieldValidationFailed.Apply(fieldValidationRate, v => v.ToString("N0"));
        handshakeReplayRejected.Apply(replayRejectedRate, v => v.ToString("N0"));
        bannedRefused.Apply(bannedRefusedRate, v => v.ToString("N0"));

        ShiftSample(inputRateThrottledSparkline.Values, inputThrottledRate.PerSec);
        ShiftSample(discreteEventThrottledSparkline.Values, discreteThrottledRate.PerSec);
        ShiftSample(fieldValidationFailedSparkline.Values, fieldValidationRate.PerSec);
        ShiftSample(handshakeReplayRejectedSparkline.Values, replayRejectedRate.PerSec);
        ShiftSample(bannedRefusedSparkline.Values, bannedRefusedRate.PerSec);

        // Per-message-type rates
        foreach ((var type, var tracker) in incomingRateTrackers)
            incomingRates[type] = tracker.Update(snap.IncomingMessages.Read(type), elapsed);
        incomingMessagesState.Apply(incomingRates);

        foreach ((var type, var tracker) in outgoingRateTrackers)
            outgoingRates[type] = tracker.Update(snap.OutgoingMessages.Read(type), elapsed);
        outgoingMessagesState.Apply(outgoingRates);
    }

    private Visual BuildVisualTree()
    {
        var metricsTable = new Table(
            TableHeaders(),
            [
                [new TextBlock("Active Peers").MinWidth(COL_LABEL), new TextBlock(() => activePeers.Value).MinWidth(COL_VALUE), "", "", "", activePeersChart.Style(STYLE_PEERS)],
                [new TextBlock("Total Connected"), new TextBlock(() => totalConnected.Value), "", "", "", ""],
                [new TextBlock("Total Disconnected"), new TextBlock(() => totalDisconnected.Value), "", "", "", ""],
                RateStatsRow("Bytes In/s", bytesIn, bytesInSparkline.Style(STYLE_INBOUND), stacked: true),
                RateStatsRow("Bytes Out/s", bytesOut, bytesOutSparkline.Style(STYLE_OUTBOUND), stacked: true),
                RateStatsRow("Packets In/s", packetsIn, packetsInSparkline.Style(STYLE_INBOUND)),
                RateStatsRow("Packets Out/s", packetsOut, packetsOutSparkline.Style(STYLE_OUTBOUND)),
                RateStatsRow("Unauth Skipped", unauthSkipped, unauthSkippedSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Send Failures", sendFailures, sendFailuresSparkline.Style(STYLE_ERROR)),
                RateStatsRow("Incoming Queue", incomingQueue, incomingQueueSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Outgoing Queue", outgoingQueue, outgoingQueueSparkline.Style(STYLE_BACKPRESSURE)),
            ]);

        var transport = new Group("Transport", metricsTable);

        var hardeningTable = new Table(
            TableHeaders(),
            [
                RateStatsRow("Pre-Auth In-Flight", preAuthInFlight, preAuthInFlightSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Pre-Auth Refused", preAuthRefused, preAuthRefusedSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Per-IP Limit Refused", preAuthIpLimitRefused, preAuthIpLimitRefusedSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Handshake Attempts Exceeded", handshakeAttemptsExceeded, handshakeAttemptsExceededSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Input Rate Throttled", inputRateThrottled, inputRateThrottledSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Discrete Event Throttled", discreteEventThrottled, discreteEventThrottledSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Field Validation Failed", fieldValidationFailed, fieldValidationFailedSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Handshake Replay Rejected", handshakeReplayRejected, handshakeReplayRejectedSparkline.Style(STYLE_BACKPRESSURE)),
                RateStatsRow("Banned Refused", bannedRefused, bannedRefusedSparkline.Style(STYLE_BACKPRESSURE)),
            ]);

        var hardening = new Group("Hardening", hardeningTable);

        Group logs = new Group("Logs",
                logControl.FollowTail(true).MaxCapacity(1000).Stretch()
            ).HorizontalAlignment(Align.Stretch);

        ScrollViewer metricsArea = new VStack(
                transport,
                hardening,
                incomingMessagesState.BuildGroup(),
                outgoingMessagesState.BuildGroup()
            ).Spacing(1)
             .Scrollable();

        return new Grid()
            .Rows(
                new RowDefinition().Height(GridLength.Star(7)),
                new RowDefinition().Height(GridLength.Star(3)))
            .RowGap(1)
            .Cell(metricsArea, row: 0, column: 0, rowSpan: 1, columnSpan: 1)
            .Cell(logs, row: 1, column: 0, rowSpan: 1, columnSpan: 1);
    }

    /// <summary>
    ///     Shared table header row with fixed column widths — reused by all tables.
    /// </summary>
    private static Visual[] TableHeaders() =>
    [
        new TextBlock("Metric").MinWidth(COL_LABEL),
        new TextBlock("Value").MinWidth(COL_VALUE),
        new TextBlock("P50").MinWidth(COL_PERCENTILE),
        new TextBlock("P95").MinWidth(COL_PERCENTILE),
        new TextBlock("P99").MinWidth(COL_PERCENTILE),
        new TextBlock("Last 60s"),
    ];

    private static Visual[] RateStatsRow(string label, RateStatsView view, Visual sparkline, bool stacked = false) =>
    [
        new TextBlock(label).MinWidth(COL_LABEL),
        new TextBlock(() => view.PerSec.Value).MinWidth(COL_VALUE),
        PercentileCell(view.P50Window, view.P50Lifetime, stacked).MinWidth(COL_PERCENTILE),
        PercentileCell(view.P95Window, view.P95Lifetime, stacked).MinWidth(COL_PERCENTILE),
        PercentileCell(view.P99Window, view.P99Lifetime, stacked).MinWidth(COL_PERCENTILE),
        sparkline,
    ];

    /// <summary>
    ///     Renders a percentile cell. When <paramref name="stacked"/> is true (byte-formatted rows),
    ///     the lifetime value is on a second line to avoid overflow. Otherwise both are inline.
    /// </summary>
    private static Visual PercentileCell(State<string> window, State<string> lifetime, bool stacked) =>
        stacked
            ? new VStack(
                new Markup(() => $"{window.Value}"),
                new Markup(() => $"[gray]({lifetime.Value})[/]"))
            : new Markup(() => $"{window.Value} [gray]({lifetime.Value})[/]");

    private static void ShiftSample(BindableList<double> values, double value)
    {
        values.RemoveAt(0);
        values.Add(value);
    }

    /// <summary>
    ///     Groups the reactive state strings for a single <see cref="RateStats" /> metric.
    ///     Each percentile level has a rolling window and lifetime value.
    /// </summary>
    private sealed class RateStatsView
    {
        public readonly State<string> PerSec = new ("-");
        public readonly State<string> P50Window = new ("-");
        public readonly State<string> P50Lifetime = new ("-");
        public readonly State<string> P95Window = new ("-");
        public readonly State<string> P95Lifetime = new ("-");
        public readonly State<string> P99Window = new ("-");
        public readonly State<string> P99Lifetime = new ("-");

        public void Apply(RateStats stats, Func<double, string> format)
        {
            PerSec.Value = format(stats.PerSec);
            P50Window.Value = format(stats.Window.P50);
            P50Lifetime.Value = format(stats.Lifetime.P50);
            P95Window.Value = format(stats.Window.P95);
            P95Lifetime.Value = format(stats.Lifetime.P95);
            P99Window.Value = format(stats.Window.P99);
            P99Lifetime.Value = format(stats.Lifetime.P99);
        }
    }

    /// <summary>
    ///     Bundles <see cref="RateStatsView" /> and <see cref="Sparkline" /> for a single message type.
    /// </summary>
    private sealed class MessageTypeView(int sparklineSamples)
    {
        public readonly RateStatsView Rate = new ();
        public readonly Sparkline Chart = new (Enumerable.Repeat(0.0, sparklineSamples));

        public void Apply(RateStats stats)
        {
            Rate.Apply(stats, v => v.ToString("N0"));
            ShiftSample(Chart.Values, stats.PerSec);
        }
    }

    /// <summary>
    ///     Display config for a per-enum-value message table. Defines title, labels, and colors.
    /// </summary>
    private sealed record MessageTableConfig<TEnum>(
        string Title,
        (TEnum Type, string Label, SparklineStyle Style)[] Entries) where TEnum: Enum;

    /// <summary>
    ///     Runtime state for a per-enum-value message table. Creates views from config,
    ///     applies snapshots, and builds the visual tree.
    /// </summary>
    private sealed class MessageTableState<TEnum> where TEnum: Enum
    {
        private readonly MessageTableConfig<TEnum> config;
        private readonly Dictionary<TEnum, MessageTypeView> views;

        public MessageTableState(MessageTableConfig<TEnum> config)
        {
            this.config = config;
            views = config.Entries.ToDictionary(e => e.Type, _ => new MessageTypeView(SPARKLINE_MAX_SAMPLES));
        }

        public void Apply(Dictionary<TEnum, RateStats>? stats)
        {
            if (stats is null)
                return;

            foreach ((TEnum type, MessageTypeView view) in views)
            {
                if (stats.TryGetValue(type, out RateStats s))
                    view.Apply(s);
            }
        }

        public Group BuildGroup()
        {
            Visual[][] rows = config.Entries
                                    .Select(e => RateStatsRow(e.Label, views[e.Type].Rate, views[e.Type].Chart.Style(e.Style)))
                                    .ToArray();

            return new Group(config.Title, new Table(TableHeaders(), rows));
        }
    }
}
