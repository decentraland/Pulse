using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;
using System.Text;

namespace DCLPulseTests.Metrics;

/// <summary>
///     Guards the stage an instrument declaration silently skips: a <see cref="PulseMetrics" /> counter
///     without a matching case in <see cref="MeterListenerMetricsCollector" /> never reaches the
///     snapshot, and one missing from <c>PrometheusFormatter</c> never reaches <c>/metrics</c>. Neither
///     is a compile error, so both are pinned here.
/// </summary>
[TestFixture]
public class ClusterMetricsTests
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
    public void HandoverInstrument_ReachesTheClustersSnapshot()
    {
        // Deltas, not absolutes: PulseMetrics instruments are static and shared across the fixture run.
        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Clusters.HANDOVERS.Add(2);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.That(after.Clusters.TotalHandovers - before.Clusters.TotalHandovers, Is.EqualTo(2));
    }

    [Test]
    public void HandoverCounter_ReachesTheMetricsEndpoint()
    {
        PulseMetrics.Clusters.HANDOVERS.Add(1);
        MetricsSnapshot snapshot = collector.TakeSnapshot();

        using var buffer = new MemoryStream();

        using (var writer = new StreamWriter(buffer, leaveOpen: true))
            PrometheusFormatter.Write(writer, snapshot);

        Assert.That(Encoding.UTF8.GetString(buffer.ToArray()), Does.Contain("dcl_pulse_cluster_handovers_total"));
    }
}
