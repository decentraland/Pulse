using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Messaging;
using Pulse.Metrics;

namespace DCLPulseTests.Metrics;

[TestFixture]
public class HistogramCollectorTests
{
    private MeterListenerMetricsCollector collector;

    [SetUp]
    public void SetUp()
    {
        // Match the constructor arities used by existing tests (see PeerSimulationTests.SetUp
        // for MessagePipe/ServerMessageCounters; mirror whatever ClientMessageCounters takes).
        var messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10));
        collector = new MeterListenerMetricsCollector(messagePipe, new ClientMessageCounters(10), new ServerMessageCounters(10));
        collector.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => collector.Dispose();

    [Test]
    public void Staleness_measurement_routes_to_the_matching_tier_histogram()
    {
        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Simulation.DELTA_STALENESS_MS[1].Record(7);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.That(after.Simulation.DeltaStalenessTier1Ms.Count - before.Simulation.DeltaStalenessTier1Ms.Count, Is.EqualTo(1));
        Assert.That(after.Simulation.DeltaStalenessTier1Ms.Sum - before.Simulation.DeltaStalenessTier1Ms.Sum, Is.EqualTo(7));
        Assert.That(after.Simulation.DeltaStalenessTier0Ms.Count, Is.EqualTo(before.Simulation.DeltaStalenessTier0Ms.Count));
        Assert.That(after.Simulation.DeltaStalenessTier2Ms.Count, Is.EqualTo(before.Simulation.DeltaStalenessTier2Ms.Count));
    }

    [Test]
    public void Tick_duration_and_drain_cycle_measurements_accumulate()
    {
        MetricsSnapshot before = collector.TakeSnapshot();

        PulseMetrics.Simulation.TICK_DURATION_US.Record(850);
        PulseMetrics.Transport.OUTGOING_DRAIN_CYCLE_US.Record(120);
        PulseMetrics.Simulation.TICK_OVERRUNS.Add(1);

        MetricsSnapshot after = collector.TakeSnapshot();

        Assert.That(after.Simulation.TickDurationUs.Count - before.Simulation.TickDurationUs.Count, Is.EqualTo(1));
        Assert.That(after.Transport.OutgoingDrainCycleUs.Count - before.Transport.OutgoingDrainCycleUs.Count, Is.EqualTo(1));
        Assert.That(after.Simulation.TotalTickOverruns - before.Simulation.TotalTickOverruns, Is.EqualTo(1));
    }
}
