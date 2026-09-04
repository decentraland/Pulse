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
    public void Resync_full_state_records_the_baseline_gap()
    {
        visibleSubjects.Add((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, 1); // first sight → PlayerJoined
        DrainAllMessages();

        PublishSnapshot(subject, seq: 7);

        (MeterListener listener, List<long> values) = CaptureHistogram("pulse.sim.resync_seq_gap_full");
        using MeterListener _ = listener;

        AddResyncRequest(observer, subject, knownSeq: 1);
        simulation.SimulateTick(peers, 2);

        Assert.That(values, Does.Contain(6L)); // 7 − 1
    }

    [Test]
    public void Resync_targeted_delta_records_the_baseline_gap_on_its_own_instrument()
    {
        PeerSimulation resyncSim = CreateSimulationWithResyncDelta();

        visibleSubjects.Add((subject, PeerViewSimulationTier.TIER_0));
        resyncSim.SimulateTick(peers, 1); // first sight
        DrainAllMessages();

        PublishSnapshot(subject, seq: 2);
        PublishSnapshot(subject, seq: 3);

        (MeterListener deltaListener, List<long> deltaValues) = CaptureHistogram("pulse.sim.resync_seq_gap_delta");
        using MeterListener _ = deltaListener;
        (MeterListener fullListener, List<long> fullValues) = CaptureHistogram("pulse.sim.resync_seq_gap_full");
        using MeterListener __ = fullListener;

        AddResyncRequest(observer, subject, knownSeq: 1);
        resyncSim.SimulateTick(peers, 2);

        Assert.That(deltaValues, Does.Contain(2L)); // 3 − 1, served from the ring
        Assert.That(fullValues, Is.Empty, "A request served by targeted delta must not count as a fallback");
    }

    [Test]
    public void Resync_gap_is_zero_when_the_client_baseline_is_already_current()
    {
        visibleSubjects.Add((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, 1);
        DrainAllMessages();

        PublishSnapshot(subject, seq: 3);

        (MeterListener listener, List<long> values) = CaptureHistogram("pulse.sim.resync_seq_gap_full");
        using MeterListener _ = listener;

        AddResyncRequest(observer, subject, knownSeq: 3);
        simulation.SimulateTick(peers, 2);

        Assert.That(values, Does.Contain(0L));
    }

    [TestCase(9u)]                    // just ahead
    [TestCase(2_147_483_652u)]        // first value past latestSeq + 2^31, where a serial-number cast flips back positive
    [TestCase(3_000_000_000u)]        // deep in the upper half of the uint range
    [TestCase(uint.MaxValue)]         // one below latestSeq modulo 2^32 — a cast reads it as a 4-seq gap
    public void Resync_gap_clamps_a_baseline_ahead_of_the_latest_seq(uint knownSeq)
    {
        // knownSeq arrives unvalidated off the wire. Any value above the subject's latest publish
        // must record 0 — inferring the ordering from the sign of a wrapped difference would let
        // anything past 2^31 register as a ~2-billion gap and swamp Sum and the +Inf bucket.
        visibleSubjects.Add((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, 1);
        DrainAllMessages();

        PublishSnapshot(subject, seq: 3);

        (MeterListener listener, List<long> values) = CaptureHistogram("pulse.sim.resync_seq_gap_full");
        using MeterListener _ = listener;

        AddResyncRequest(observer, subject, knownSeq: knownSeq);
        simulation.SimulateTick(peers, 2);

        Assert.That(values, Is.EqualTo(new[] { 0L }));
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
