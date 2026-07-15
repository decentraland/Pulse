using NSubstitute;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Diagnostics.Metrics;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    /// <summary>
    ///     Captures raw measurements for one instrument on the shared DCLPulse meter.
    ///     Dispose the listener at test end. Values-based assertions (Does.Contain)
    ///     keep the tests robust against unrelated fixtures recording to the same
    ///     static instrument.
    /// </summary>
    private static (MeterListener Listener, List<long> Values) CaptureHistogram(string instrumentName)
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
    public void Delta_send_records_publish_to_fanout_staleness_on_tier0()
    {
        (MeterListener listener, List<long> values) = CaptureHistogram("pulse.sim.delta_staleness_tier0_ms");
        using MeterListener _ = listener;

        visibleSubjects.Add((subject, new PeerViewSimulationTier(0)));
        simulation.SimulateTick(peers, 1); // first sight → PlayerJoined, no delta

        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 2, serverTick: 20));
        timeProvider.MonotonicTime.Returns(60u);
        simulation.SimulateTick(peers, 2); // delta seq1→seq2

        Assert.That(values, Does.Contain(40L)); // 60 − 20
    }

    [Test]
    public void Staleness_subtraction_is_wrap_safe_across_uint_rollover()
    {
        (MeterListener listener, List<long> values) = CaptureHistogram("pulse.sim.delta_staleness_tier0_ms");
        using MeterListener _ = listener;

        visibleSubjects.Add((subject, new PeerViewSimulationTier(0)));
        simulation.SimulateTick(peers, 1);

        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 2, serverTick: uint.MaxValue - 9));
        timeProvider.MonotonicTime.Returns(30u); // clock wrapped: real elapsed = 40 ms
        simulation.SimulateTick(peers, 2);

        Assert.That(values, Does.Contain(40L));
    }

    [Test]
    public void Tier1_subject_records_on_the_tier1_instrument()
    {
        (MeterListener tier0Listener, List<long> tier0Values) = CaptureHistogram("pulse.sim.delta_staleness_tier0_ms");
        using MeterListener _ = tier0Listener;
        (MeterListener tier1Listener, List<long> tier1Values) = CaptureHistogram("pulse.sim.delta_staleness_tier1_ms");
        using MeterListener __ = tier1Listener;

        visibleSubjects.Add((subject, new PeerViewSimulationTier(1)));
        simulation.SimulateTick(peers, 2); // tier1 divisor = 2 → due on even ticks; first sight

        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 2, serverTick: 20));
        timeProvider.MonotonicTime.Returns(50u);
        simulation.SimulateTick(peers, 4); // next due tier1 tick → delta

        Assert.That(tier1Values, Does.Contain(30L)); // 50 − 20
        Assert.That(tier0Values, Does.Not.Contain(30L));
    }

    [Test]
    public void Resync_delta_does_not_record_staleness()
    {
        // Resync-delta path fires SendDelta with fromResync: true, which must be excluded.
        PeerSimulation resyncSim = CreateSimulationWithResyncDelta();

        visibleSubjects.Add((subject, new PeerViewSimulationTier(0)));
        resyncSim.SimulateTick(peers, 1); // first sight

        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 2, serverTick: 20));
        timeProvider.MonotonicTime.Returns(60u);

        (MeterListener listener, List<long> values) = CaptureHistogram("pulse.sim.delta_staleness_tier0_ms");
        using MeterListener _ = listener;

        // Pending resync forces the targeted-delta path (fromResync: true).
        AddResyncRequest(observer, subject, knownSeq: 1);
        resyncSim.SimulateTick(peers, 2);

        Assert.That(values, Is.Empty);
    }
}
