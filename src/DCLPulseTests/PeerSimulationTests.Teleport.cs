using Decentraland.Pulse;
using NSubstitute;
using Pulse.Peers;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void Teleport_BroadcastedToObserver_WhenSubjectTeleports()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // consume PlayerJoined

        teleportBoard.Publish(subject, new Vector3(10, 20, 30), serverTick: 5000);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage teleportMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        Assert.That(teleportMsg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
        Assert.That(teleportMsg.Message.Teleported.SubjectId, Is.EqualTo(subject.Value));
        Assert.That(teleportMsg.Message.Teleported.ServerTick, Is.EqualTo(5000u));
        Assert.That(teleportMsg.Message.Teleported.Position.X, Is.EqualTo(10f));
        Assert.That(teleportMsg.Message.Teleported.Position.Y, Is.EqualTo(20f));
        Assert.That(teleportMsg.Message.Teleported.Position.Z, Is.EqualTo(30f));
    }

    [Test]
    public void Teleport_NotSentAgain_WhenAlreadySynced()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        teleportBoard.Publish(subject, new Vector3(10, 20, 30), serverTick: 5000);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // consume Teleported + delta

        // Same teleport still in the board, next tick should not re-send
        PublishSnapshot(subject, seq: 3);
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

        teleportBoard.Publish(subject, new Vector3(10, 20, 30), serverTick: 5000);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // New teleport with a different server tick
        teleportBoard.Publish(subject, new Vector3(50, 60, 70), serverTick: 6000);
        PublishSnapshot(subject, seq: 3);
        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage teleportMsg = messages.First(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.Teleported);
        Assert.That(teleportMsg.Message.Teleported.ServerTick, Is.EqualTo(6000u));
        Assert.That(teleportMsg.Message.Teleported.Position.X, Is.EqualTo(50f));
    }

    [Test]
    public void Teleport_NotSent_WhenSubjectIsNewAndJoinsWithPlayerJoined()
    {
        // On first visibility, PlayerJoined carries the full state — no separate Teleported needed
        teleportBoard.Publish(subject, new Vector3(10, 20, 30), serverTick: 5000);
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
        DrainAllMessages(); // consume PlayerJoined for both observers

        teleportBoard.Publish(subject, new Vector3(10, 20, 30), serverTick: 5000);
        PublishSnapshot(subject, seq: 2);
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

        teleportBoard.Publish(subject, new Vector3(10, 20, 30), serverTick: 5000);
        PublishSnapshot(subject, seq: 2);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages(); // observer received Teleported

        // Observer B joins later — subject is still in the board with the same teleport
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

    [Test]
    public void Teleport_BoardClearedOnDisconnect()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        teleportBoard.Publish(subject, new Vector3(10, 20, 30), serverTick: 5000);

        // Verify the teleport is in the board
        Assert.That(teleportBoard.TryRead(subject, out _), Is.True);

        // Simulate subject disconnecting and being cleaned up
        peers[subject] = new PeerState(PeerConnectionState.DISCONNECTING);
        peers[subject].TransportState = peers[subject].TransportState with { DisconnectionTime = 0 };
        timeProvider.MonotonicTime.Returns(10000u); // past PEER_DISCONNECTION_CLEAN_TIMEOUT
        snapshotBoard.SetActive(subject);

        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(teleportBoard.TryRead(subject, out _), Is.False);
    }
}
