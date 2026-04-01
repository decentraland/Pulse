using Decentraland.Pulse;
using NSubstitute;
using Pulse.Peers;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    private void PublishTeleportSnapshot(PeerIndex peer, uint seq, Vector3 globalPosition)
    {
        snapshotBoard.SetActive(peer);

        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: seq, ServerTick: seq * 10,
            Parcel: 0,
            LocalPosition: globalPosition, Velocity: Vector3.Zero,
            GlobalPosition: globalPosition,
            RotationY: 0f, MovementBlend: 0f, JumpCount: 0, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.Grounded,
            GlideState: GlideState.PropClosed,
            IsTeleport: true));
    }

    [Test]
    public void Teleport_BroadcastedToObserver_WhenSubjectTeleports()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // consume PlayerJoined

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage teleportMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        Assert.That(teleportMsg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
        Assert.That(teleportMsg.Message.Teleported.SubjectId, Is.EqualTo(subject.Value));
        Assert.That(teleportMsg.Message.Teleported.Sequence, Is.EqualTo(2u));
        Assert.That(teleportMsg.Message.Teleported.ServerTick, Is.EqualTo(20u));
        Assert.That(teleportMsg.Message.Teleported.State.Position.X, Is.EqualTo(10f));
        Assert.That(teleportMsg.Message.Teleported.State.Position.Y, Is.EqualTo(20f));
        Assert.That(teleportMsg.Message.Teleported.State.Position.Z, Is.EqualTo(30f));
    }

    [Test]
    public void Teleport_ReplacesStateDelta()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateDelta), Is.False);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
    }

    [Test]
    public void Teleport_ReplacesResync()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Queue a resync, then publish a teleport snapshot
        AddResyncRequest(observer, subject, knownSeq: 1);
        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerStateFull), Is.False);
    }

    [Test]
    public void Teleport_NotSentAgain_OnSubsequentTick()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Next tick, same snapshot — seq hasn't changed, so teleport is not re-sent
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.False);
    }

    [Test]
    public void Teleport_SentForConsecutiveTeleports()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> first = DrainAllMessages();
        Assert.That(first.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.True);

        // Second consecutive teleport without normal movement in between
        PublishTeleportSnapshot(subject, seq: 3, new Vector3(50, 60, 70));
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> second = DrainAllMessages();
        OutgoingMessage teleportMsg = second.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        Assert.That(teleportMsg.Message.Teleported.State.Position.X, Is.EqualTo(50f));
    }

    [Test]
    public void Teleport_NotSentAfterNormalMovementResumes()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Normal movement resumes — IsTeleport is false
        PublishSnapshot(subject, seq: 3, position: new Vector3(11, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.False);
    }

    [Test]
    public void Teleport_SentAgain_WhenNewTeleportOccurs()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Normal movement in between
        PublishSnapshot(subject, seq: 3, position: new Vector3(11, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 2);
        DrainAllMessages();

        // Second teleport
        PublishTeleportSnapshot(subject, seq: 4, new Vector3(50, 60, 70));
        simulation.SimulateTick(peers, tickCounter: 3);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage teleportMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        Assert.That(teleportMsg.Message.Teleported.State.Position.X, Is.EqualTo(50f));
    }

    [Test]
    public void Teleport_NotSent_WhenSubjectIsNewAndJoinsWithPlayerJoined()
    {
        // Subject teleported before observer sees them — PlayerJoined carries the full state
        PublishTeleportSnapshot(subject, seq: 1, new Vector3(10, 20, 30));
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined), Is.True);
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.False);
    }

    [Test]
    public void Teleport_BroadcastedToMultipleObservers()
    {
        var observer2 = new PeerIndex(2);
        peers[observer2] = new PeerState(PeerConnectionState.AUTHENTICATED);
        PublishSnapshot(observer2, seq: 1);

        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        List<OutgoingMessage> teleportMessages = messages
            .Where(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported)
            .ToList();

        Assert.That(teleportMessages.Count, Is.EqualTo(2));
        Assert.That(teleportMessages.Any(m => m.To == observer), Is.True);
        Assert.That(teleportMessages.Any(m => m.To == observer2), Is.True);
    }

    [Test]
    public void Teleport_NotSentToNewObserver_WhenTeleportHappenedBeforeJoining()
    {
        // Observer A already sees subject and receives the teleport
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishTeleportSnapshot(subject, seq: 2, new Vector3(10, 20, 30));
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Observer B joins later — subject's last snapshot still has IsTeleport=true
        var lateObserver = new PeerIndex(2);
        peers[lateObserver] = new PeerState(PeerConnectionState.AUTHENTICATED);
        PublishSnapshot(lateObserver, seq: 1);

        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();

        // Late observer gets PlayerJoined (full state) but NOT the stale Teleported
        List<OutgoingMessage> lateMessages = messages.Where(m => m.To == lateObserver).ToList();
        Assert.That(lateMessages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined), Is.True);
        Assert.That(lateMessages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported), Is.False);
    }
}