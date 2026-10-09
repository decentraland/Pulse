using System.Text;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;

namespace DCLPulseTests.Metrics;

[TestFixture]
public class InterestSnapshotMetricsTests
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
    public void InterestSnapshotEvictedInstrument_ReachesTheMetricsEndpoint()
    {
        MetricsSnapshot before = collector.TakeSnapshot();
        PulseMetrics.Simulation.INTEREST_SNAPSHOT_EVICTED.Add(2);
        PulseMetrics.Simulation.INTEREST_SNAPSHOT_EVICTED.Add(3);
        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(after.Simulation.TotalInterestSnapshotEvicted - before.Simulation.TotalInterestSnapshotEvicted,
                Is.EqualTo(5));
            Assert.That(FormatSnapshot(), Does.Contain(
                $"dcl_pulse_interest_snapshot_evicted_total {after.Simulation.TotalInterestSnapshotEvicted}{Environment.NewLine}"));
        });
    }

    [Test]
    public void InterestSnapshotEvictedCounter_IsExportedWithoutPeerLabelsWhenZero()
    {
        string output = FormatSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(output, Does.Contain("# TYPE dcl_pulse_interest_snapshot_evicted_total counter"));
            Assert.That(output, Does.Contain($"dcl_pulse_interest_snapshot_evicted_total 0{Environment.NewLine}"));
            Assert.That(output, Does.Not.Contain("dcl_pulse_interest_snapshot_evicted_total{"));
        });
    }

    private string FormatSnapshot()
    {
        using var buffer = new MemoryStream();

        using (var writer = new StreamWriter(buffer, leaveOpen: true))
            PrometheusFormatter.Write(writer, collector.TakeSnapshot());

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
