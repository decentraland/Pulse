using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Transport.Hardening;
using System.Text;

namespace DCLPulseTests.Metrics;

/// <summary>
///     Guards the stage an instrument declaration silently skips: a <see cref="PulseMetrics" /> counter
///     without a matching case in <see cref="MeterListenerMetricsCollector" /> never reaches the snapshot,
///     and one missing from <c>PrometheusFormatter</c> never reaches <c>/metrics</c>. Both are invisible
///     at compile time, so they are pinned here.
/// </summary>
[TestFixture]
public class HardeningMetricsTests
{
    private MeterListenerMetricsCollector collector;

    [SetUp]
    public void SetUp()
    {
        var messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters());
        collector = new MeterListenerMetricsCollector(messagePipe, new ClientMessageCounters(), new ServerMessageCounters());
        collector.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => collector.Dispose();

    [Test]
    public void IpLimitInstruments_ReachTheHardeningSnapshot()
    {
        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Hardening.IP_LIMIT_REFUSED.Add(3, PulseMetrics.Hardening.Tag(ConnectionClass.PLAYER));
        PulseMetrics.Hardening.IP_LIMIT_REFUSED.Add(1, PulseMetrics.Hardening.Tag(ConnectionClass.SCENE_LISTENER));
        PulseMetrics.Hardening.IP_LIMIT_WHITELIST_BYPASS.Add(2);
        PulseMetrics.Hardening.IP_LIMIT_TRACKED_IPS.Add(5);
        PulseMetrics.Hardening.IP_LIMIT_TRACKED_IPS.Add(-1);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(Refused(after, ConnectionClass.PLAYER) - Refused(before, ConnectionClass.PLAYER), Is.EqualTo(3),
                "The class tag must bucket the measurement, not pool it");
            Assert.That(Refused(after, ConnectionClass.SCENE_LISTENER) - Refused(before, ConnectionClass.SCENE_LISTENER), Is.EqualTo(1));
            Assert.That(after.Hardening.TotalIpLimitRefused - before.Hardening.TotalIpLimitRefused, Is.EqualTo(4),
                "The total sums every class");
            Assert.That(after.Hardening.TotalIpLimitWhitelistBypass - before.Hardening.TotalIpLimitWhitelistBypass, Is.EqualTo(2));
            Assert.That(after.Hardening.IpLimitTrackedIps - before.Hardening.IpLimitTrackedIps, Is.EqualTo(4));
        });
    }

    [Test]
    public void Write_IpLimitSeries_AreExposed()
    {
        string output = Format(new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot { ByTransport = new MetricsSnapshot.PerTransportCounters[2] },
            Hardening = new MetricsSnapshot.HardeningSnapshot
            {
                IpLimitRefusedByClass = [3, 1],
                TotalIpLimitWhitelistBypass = 2,
                IpLimitTrackedIps = 4,
            },
            IncomingMessages = new ClientMessageCounters(),
            OutgoingMessages = new ServerMessageCounters(),
        });

        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("dcl_pulse_ip_limit_refused_total{class=\"player\"} 3"));
            Assert.That(output, Does.Contain("dcl_pulse_ip_limit_refused_total{class=\"scene_listener\"} 1"));
            Assert.That(output, Does.Contain("dcl_pulse_ip_limit_whitelist_bypass_total 2"));
            Assert.That(output, Does.Contain("dcl_pulse_ip_limit_tracked_ips 4"));
        });
    }

    /// <summary>
    ///     Feature-flag health is carried by logs, not metrics. Pinned so a series cannot creep back
    ///     onto <c>/metrics</c> unnoticed.
    /// </summary>
    [Test]
    public void Write_FeatureFlagSeries_AreNotExposed()
    {
        string output = Format(new MetricsSnapshot
        {
            Transport = new MetricsSnapshot.TransportSnapshot { ByTransport = new MetricsSnapshot.PerTransportCounters[2] },
            IncomingMessages = new ClientMessageCounters(),
            OutgoingMessages = new ServerMessageCounters(),
        });

        Assert.That(output, Does.Not.Contain("feature_flags"));
    }

    /// <summary>
    ///     Per-IP refusals recorded against one connection class, read out of a snapshot the way the
    ///     Prometheus writer reads them.
    /// </summary>
    private static long Refused(MetricsSnapshot snap, ConnectionClass connectionClass) =>
        snap.Hardening.IpLimitRefusedByClass?[(int)connectionClass] ?? 0;

    private static string Format(MetricsSnapshot snap)
    {
        using var stream = new MemoryStream();

        using (var writer = new StreamWriter(stream, leaveOpen: true))
            PrometheusFormatter.Write(writer, snap);

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
