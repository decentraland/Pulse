using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RealmRace_SubjectMovesAfterSpatialCollection_RetiresOnlyExistingView(bool previouslyVisible)
    {
        UseSpatialInterest();
        IAreaOfInterest spatial = areaOfInterest;
        bool moveAfterCollection = false;
        areaOfInterest = Substitute.For<IAreaOfInterest>();
        areaOfInterest.When(aoi => aoi.GetVisibleSubjects(Arg.Any<PeerIndex>(), Arg.Any<PeerSnapshot>(), Arg.Any<IInterestCollector>()))
            .Do(call =>
            {
                spatial.GetVisibleSubjects(call.ArgAt<PeerIndex>(0), call.ArgAt<PeerSnapshot>(1), call.ArgAt<IInterestCollector>(2));
                if (moveAfterCollection)
                {
                    // Deterministic cross-worker interleave: collection sees old membership,
                    // then the simulation reads the snapshot published in the new realm.
                    PlaceInRealm(subject, "other", 3, teleport: true);
                    moveAfterCollection = false;
                }
            });
        simulation = new PeerSimulation(areaOfInterest, snapshotBoard, realmGrids, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(), profileBoard, peerIndexAllocator,
            Substitute.For<ILogger<PeerSimulation>>());
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        if (previouslyVisible)
        {
            simulation.SimulateTick(peers, 0);
            DrainAllMessages();
        }

        moveAfterCollection = true;
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(message => message.Message.MessageCase), Is.EqualTo(previouslyVisible
            ? new[] { ServerMessage.MessageOneofCase.PlayerLeft }
            : Array.Empty<ServerMessage.MessageOneofCase>()));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));

        for (uint tick = 2; tick <= FirstSweepTickAfter(1); tick++)
            simulation.SimulateTick(peers, tick);
        Assert.That(DrainAllMessages(), Is.Empty, "Removed views must not emit another leave in the stale sweep");

        PlaceInRealm(subject, "old", 4, teleport: true);
        simulation.SimulateTick(peers, FirstSweepTickAfter(1) + 1);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealmRace_SubjectMovesAfterListenerCollection_RetiresOnlyExistingView(bool previouslyVisible)
    {
        MakeSceneListener(observer, new Dictionary<string, int[]> { ["old"] = [0] });
        PlaceInRealm(subject, "old", 2);
        if (previouslyVisible)
        {
            simulation.SimulateTick(peers, 0);
            Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));
        }

        // The state a cross-worker teleport leaves between collection and TryRead: still
        // indexed in the announced realm's grid under a snapshot naming an unannounced realm.
        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 3, serverTick: 30, realm: "new", isTeleport: true));
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(message => message.Message.MessageCase), Is.EqualTo(previouslyVisible
            ? new[] { ServerMessage.MessageOneofCase.PlayerLeft }
            : Array.Empty<ServerMessage.MessageOneofCase>()));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));

        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty, "An out-of-AoI subject must never be announced");
    }

    [Test]
    public void RealmRace_PlayerWithoutRealm_RejectsRealmedSubject()
    {
        PlaceInRealm(subject, "other", 2);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    [Test]
    public void RealmRace_TierDelayedSubject_IsRejectedBeforeSendingANewBaseline()
    {
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_2));
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        PlaceInRealm(subject, "other", 3, teleport: true);
        for (uint tick = 1; tick < 4; tick++)
            simulation.SimulateTick(peers, tick);
        Assert.That(DrainAllMessages(), Is.Empty);
        Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Realm, Is.EqualTo("old"));
        simulation.SimulateTick(peers, 4);
        Assert.That(DrainSingleMessage().Message.PlayerLeft.SubjectId, Is.EqualTo(subject.Value));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
    }

    [Test]
    public void RealmTransition_SelfMirror_ReseedsAndKeepsItsViewStamped()
    {
        UseSpatialInterest();
        simulation = new PeerSimulation(areaOfInterest, snapshotBoard, realmGrids, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(), profileBoard, peerIndexAllocator,
            Substitute.For<ILogger<PeerSimulation>>(), selfMirrorEnabled: true);
        PlaceInRealm(observer, "old", 2);
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.UserId, Is.EqualTo(PeerSimulation.SELF_MIRROR_WALLET_ID));
        PlaceInRealm(observer, "new", 3, teleport: true);
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(message => message.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
        }));
        Assert.That(messages[1].Message.PlayerJoined.Realm, Is.EqualTo("new"));
        Assert.That(messages[1].Message.PlayerJoined.UserId, Is.EqualTo(PeerSimulation.SELF_MIRROR_WALLET_ID));
        Assert.That(simulation.observerViews[observer][observer].LastSeenTick, Is.EqualTo(1u));
        for (uint tick = 2; tick <= FirstSweepTickAfter(1); tick++)
            simulation.SimulateTick(peers, tick);
        Assert.That(DrainAllMessages(), Is.Empty);
        Assert.That(simulation.observerViews[observer], Does.ContainKey(observer));
    }

    [Test]
    public void RealmTransition_InheritedEmoteSurvivesGenerationChangeAndHistoryEviction()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2, emote: new EmoteState("wave", StartSeq: 2, StartTick: 20));
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        PlaceInRealm(observer, "new", 3, teleport: true);
        PlaceInRealm(subject, "new", 3, teleport: true);
        for (uint seq = 4; seq <= RING_CAPACITY * 2; seq++)
            snapshotBoard.Publish(subject, TestSnapshots.Make(seq: seq));
        Assert.That(snapshotBoard.TryRead(subject, 2, out _), Is.False);
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(message => message.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
            ServerMessage.MessageOneofCase.EmoteStarted,
        }));
        Assert.That(messages[2].Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(messages[2].Message.EmoteStarted.Sequence, Is.EqualTo((uint)RING_CAPACITY * 2));
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    [Test]
    public void RealmTransition_ObserverCleanupAndSlotReuse_UsesNewPeerStateGeneration()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        Assert.That(peers[observer].LastObservedRealmGeneration, Is.EqualTo(1ul));
        peers[observer].ConnectionState = PeerConnectionState.DISCONNECTING;
        peers[observer].TransportState = new PeerTransportState(ConnectionTime: 0, DisconnectionTime: 0);
        timeProvider.MonotonicTime.Returns(6000u);
        simulation.SimulateTick(peers, 1);
        Assert.That(peers, Does.Not.ContainKey(observer));
        Assert.That(simulation.observerViews, Does.Not.ContainKey(observer));

        peers[observer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        Assert.That(peers[observer].LastObservedRealmGeneration, Is.Zero);
        snapshotBoard.SetActive(observer);
        PlaceInRealm(observer, "new", 1);
        PlaceInRealm(subject, "new", 3);
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("new"));
        Assert.That(peers[observer].LastObservedRealmGeneration, Is.EqualTo(1ul));
    }
}
