using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void Resync_SendsFullStateWhenKnownSeqEvictedFromRing()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined

        // Advance the subject well past ring capacity so seq 1 is evicted
        for (uint s = 2; s <= RING_CAPACITY + 5; s++)
            PublishSnapshot(subject, seq: s);

        // Observer requests resync from seq 1, which is no longer in the ring
        AddResyncRequest(observer, subject, knownSeq: 1);

        simulation.SimulateTick(peers, tickCounter: 1);

        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.To, Is.EqualTo(observer));
        Assert.That(msg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateFull));
        Assert.That(msg.Message.PlayerStateFull.SubjectId, Is.EqualTo(subject.Value));
        Assert.That(msg.Message.PlayerStateFull.Sequence, Is.EqualTo(RING_CAPACITY + 5));
    }

    [Test]
    public void Resync_ViewBaselineAdvancesToCurrentAfterResync()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined

        PublishSnapshot(subject, seq: 2, position: new Vector3(1f, 0f, 0f));
        AddResyncRequest(observer, subject, knownSeq: 1);

        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // resync delta

        // Next tick with new movement — should produce a normal delta from seq 2 baseline
        PublishSnapshot(subject, seq: 3, position: new Vector3(1f, 2f, 0f));
        simulation.SimulateTick(peers, tickCounter: 2);

        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.PacketMode, Is.EqualTo(ITransport.PacketMode.UNRELIABLE_SEQUENCED));
        Assert.That(msg.Message.PlayerStateDelta.NewSeq, Is.EqualTo(3u));

        // Only Y changed from seq 2 to seq 3
        Assert.That(msg.Message.PlayerStateDelta.HasPositionX, Is.False);
        Assert.That(msg.Message.PlayerStateDelta.PositionYQuantized, Is.EqualTo(2f));
    }

    [Test]
    public void Resync_ClearedWhenSubjectIsNew()
    {
        // Queue a resync before the subject is ever visible
        AddResyncRequest(observer, subject, knownSeq: 1);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);

        // Should send PlayerJoined, not a resync response
        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));
    }

    [Test]
    public void Resync_LeftoversForInvisibleSubjectsClearedAfterTick()
    {
        var invisible = new PeerIndex(99);
        PublishSnapshot(invisible, seq: 1);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Queue resyncs for both a visible and an invisible subject
        AddResyncRequest(observer, subject, knownSeq: 1);
        AddResyncRequest(observer, invisible, knownSeq: 1);

        PublishSnapshot(subject, seq: 2, position: new Vector3(1f, 0f, 0f));
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // The resync dictionary should have been cleared — invisible request doesn't linger
        Assert.That(peers[observer].ResyncRequests, Is.Not.Null);
        Assert.That(peers[observer].ResyncRequests, Is.Empty);
    }

    [Test]
    public void Resync_NullResyncRequestsDoesNotCrash()
    {
        // ResyncRequests is null by default — normal ticks should work fine
        Assert.That(peers[observer].ResyncRequests, Is.Null);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);

        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));
    }

    // SimulationSteps = [50, 100, 200] → divisors = [1, 2, 4]
    // TIER_1 fires on even ticks, TIER_2 on ticks divisible by 4.

    [Test]
    public void Resync_NotDroppedWhenTier1SubjectIsGated()
    {
        // Establish view at tick 0 (TIER_1 fires on even ticks)
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_1));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined

        PublishSnapshot(subject, seq: 2, position: new Vector3(3f, 0f, 0f));
        AddResyncRequest(observer, subject, knownSeq: 1);

        // Tick 1 is odd — TIER_1 is not due. The resync must not be dropped.
        simulation.SimulateTick(peers, tickCounter: 1);

        // Tick 2 is even — TIER_1 is due. The resync should be processed now.
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();

        Assert.That(messages, Has.Count.GreaterThan(0),
            "Resync response was silently dropped because the tier was not due on the tick it was queued");

        OutgoingMessage resyncMsg = messages.First(m =>
            m.Message.MessageCase is ServerMessage.MessageOneofCase.PlayerStateDelta
                or ServerMessage.MessageOneofCase.PlayerStateFull);

        Assert.That(resyncMsg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
    }

    [Test]
    public void Resync_NotDroppedWhenTier2SubjectIsGated()
    {
        // Establish view at tick 0 (TIER_2 fires on ticks divisible by 4)
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_2));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined

        PublishSnapshot(subject, seq: 2, position: new Vector3(7f, 0f, 0f));
        AddResyncRequest(observer, subject, knownSeq: 1);

        // Ticks 1, 2, 3 are not due for TIER_2. The resync must survive.
        simulation.SimulateTick(peers, tickCounter: 1);
        simulation.SimulateTick(peers, tickCounter: 2);
        simulation.SimulateTick(peers, tickCounter: 3);

        // Tick 4 is due for TIER_2. The resync should be processed now.
        simulation.SimulateTick(peers, tickCounter: 4);

        List<OutgoingMessage> messages = DrainAllMessages();

        Assert.That(messages, Has.Count.GreaterThan(0),
            "Resync response was silently dropped because the tier was not due on the ticks it was queued");

        OutgoingMessage resyncMsg = messages.First(m =>
            m.Message.MessageCase is ServerMessage.MessageOneofCase.PlayerStateDelta
                or ServerMessage.MessageOneofCase.PlayerStateFull);

        Assert.That(resyncMsg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
    }

    [Test]
    public void Resync_SendsResponseWhenTargetedDeltaIsNull()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined at seq 1

        // Publish a new seq with identical field values — only the seq number advances
        PublishSnapshot(subject, seq: 2);

        // Client requests resync from seq 1. The diff from seq 1 to seq 2 has no field
        // changes (both are Zero/default), so PeerViewDiff.CreateMessage returns null.
        // The server must still respond — the client is stuck in resync-pending mode.
        AddResyncRequest(observer, subject, knownSeq: 1);

        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(messagePipe.TryReadOutgoingMessage(out OutgoingMessage msg), Is.True,
            "No response sent — client is stuck in resync-pending mode because the targeted delta was null");

        Assert.That(msg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
    }
}
