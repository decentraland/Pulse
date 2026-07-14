using Pulse.Peers;
using Pulse.Transport;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DCLPulseTests.Metrics;

[TestFixture]
public class LoopTimingMetricsTests
{
    private static (MeterListener Listener, List<long> Values) Capture(string instrumentName)
    {
        var values = new List<long>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "DCLPulse" && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Add(value));
        listener.Start();
        return (listener, values);
    }

    [Test]
    public void Tick_duration_is_recorded_in_microseconds()
    {
        (MeterListener listener, List<long> values) = Capture("pulse.sim.tick_duration_us");
        using MeterListener _ = listener;

        PeersManager.RecordTickDuration(Stopwatch.GetTimestamp(), baseTickMs: uint.MaxValue);

        Assert.That(values, Has.Count.EqualTo(1));
        Assert.That(values[0], Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Tick_exceeding_budget_increments_overrun_counter()
    {
        (MeterListener listener, List<long> values) = Capture("pulse.sim.tick_overruns");
        using MeterListener _ = listener;

        // baseTickMs = 0 → any nonzero elapsed time is an overrun.
        long start = Stopwatch.GetTimestamp();
        Thread.Sleep(1);
        PeersManager.RecordTickDuration(start, baseTickMs: 0);

        Assert.That(values, Has.Count.EqualTo(1));
    }

    [Test]
    public void Tick_within_budget_does_not_increment_overrun_counter()
    {
        (MeterListener listener, List<long> values) = Capture("pulse.sim.tick_overruns");
        using MeterListener _ = listener;

        PeersManager.RecordTickDuration(Stopwatch.GetTimestamp(), baseTickMs: uint.MaxValue);

        Assert.That(values, Is.Empty);
    }

    [Test]
    public void Empty_drain_cycle_is_not_recorded()
    {
        (MeterListener listener, List<long> values) = Capture("pulse.transport.outgoing_drain_cycle_us");
        using MeterListener _ = listener;

        ENetHostedService.RecordDrainCycle(Stopwatch.GetTimestamp(), drained: 0);

        Assert.That(values, Is.Empty);
    }

    [Test]
    public void Nonempty_drain_cycle_records_duration()
    {
        (MeterListener listener, List<long> values) = Capture("pulse.transport.outgoing_drain_cycle_us");
        using MeterListener _ = listener;

        ENetHostedService.RecordDrainCycle(Stopwatch.GetTimestamp(), drained: 3);

        Assert.That(values, Has.Count.EqualTo(1));
        Assert.That(values[0], Is.GreaterThanOrEqualTo(0));
    }
}
