using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void PlayerLeft_SentWhenSubjectLeavesInterestSet()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject disappears from interest set
        SetVisibleSubjects();

        // Sweep fires at multiples of SWEEP_CHECK_INTERVAL and the stale check is strict
        // greater-than, so the first sweep that catches a view stamped at tick 0 is the one
        // FirstSweepTickAfter derives.
        simulation.SimulateTick(peers, tickCounter: FirstSweepTickAfter(0));

        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.To, Is.EqualTo(observer));
        Assert.That(msg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerLeft));
        Assert.That(msg.Message.PlayerLeft.SubjectId, Is.EqualTo(subject.Value));
    }

    [Test]
    public void PlayerLeft_NotSentIfSubjectReappearsBeforeSweep()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject disappears for a few ticks
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: 1);
        simulation.SimulateTick(peers, tickCounter: 2);

        // Subject reappears before sweep would catch the stale view
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        PublishSnapshot(subject, seq: 2);

        // Re-stamp the view on a tick before the sweep
        simulation.SimulateTick(peers, tickCounter: FirstSweepTickAfter(0) - 1);
        DrainAllMessages();

        // Sweep fires — view was re-stamped recently so it should not be swept
        simulation.SimulateTick(peers, tickCounter: FirstSweepTickAfter(0));

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft), Is.False);
    }

    [Test]
    public void PlayerLeft_SentForMultipleSubjects()
    {
        var subject2 = new PeerIndex(2);
        PublishSnapshot(subject2, seq: 1);
        identityBoard.Set(subject2, "0xSUBJECT2_WALLET");

        SetVisibleSubjects(
            (subject, PeerViewSimulationTier.TIER_0),
            (subject2, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Both subjects leave
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: FirstSweepTickAfter(0));

        List<OutgoingMessage> messages = DrainAllMessages();

        var leftIds = messages
                     .Where(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft)
                     .Select(m => m.Message.PlayerLeft.SubjectId)
                     .ToList();

        Assert.That(leftIds, Has.Count.EqualTo(2));
        Assert.That(leftIds, Does.Contain(subject.Value));
        Assert.That(leftIds, Does.Contain(subject2.Value));
    }

    /// <summary>
    ///     Pins the eviction bound the two sweep constants are chosen to produce: a view survives
    ///     its own staleness threshold exactly (the predicate is a strict greater-than) and is gone
    ///     within one further check interval. Loosening either constant without re-deriving the
    ///     documented 3.05–4.0 s window breaks this.
    /// </summary>
    [Test]
    public void StaleView_SurvivesTheStalenessThreshold_ThenSweptWithinOneCheckInterval()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        SetVisibleSubjects();

        for (uint tick = 1; tick <= VIEW_STALE_TICKS; tick++)
            simulation.SimulateTick(peers, tick);

        Assert.That(DrainAllMessages().Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft), Is.False,
            $"a view exactly VIEW_STALE_TICKS ({VIEW_STALE_TICKS}) ticks old must not be swept yet");

        for (uint tick = VIEW_STALE_TICKS + 1; tick <= VIEW_STALE_TICKS + SWEEP_CHECK_INTERVAL; tick++)
            simulation.SimulateTick(peers, tick);

        Assert.That(DrainAllMessages().Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft), Is.True,
            "a stale view must be swept within SWEEP_CHECK_INTERVAL ticks of crossing VIEW_STALE_TICKS");
    }
}
