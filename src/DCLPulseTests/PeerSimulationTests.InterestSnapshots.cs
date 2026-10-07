using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    private Action? interestCollectionInterleave;

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSnapshot_RealmChangeAfterCollection_DeliversAcceptedRealmThenSweeps(bool previouslyVisible)
    {
        PrepareCapturedInterest(previouslyVisible);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => PublishInterestSnapshot(4, "new", new Vector3(9, 0, 0), teleport: true));

        simulation.SimulateTick(peers, 1);

        AssertAcceptedInterestMessage(previouslyVisible, 3, 2);
        Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Realm, Is.EqualTo("old"));
        Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.EqualTo(1u));
        AssertAcceptedViewSweptOnce(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSnapshot_MovementAfterCollection_DeliversAcceptedPoseAndTier(bool previouslyVisible)
    {
        PrepareCapturedInterest(previouslyVisible);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => PublishInterestSnapshot(4, "old", new Vector3(9, 0, 0),
            globalPosition: new Vector3(200, 0, 0)));

        simulation.SimulateTick(peers, 1);

        AssertAcceptedInterestMessage(previouslyVisible, 3, 2);
        Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.GlobalPosition,
            Is.EqualTo(new Vector3(2, 0, 0)));
        AssertAcceptedViewSweptOnce(1);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InterestSnapshot_EventAfterCollection_IsDeferredUntilNextQuery(bool previouslyVisible, bool teleport)
    {
        PrepareCapturedInterest(previouslyVisible);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => PublishInterestSnapshot(4, "old", new Vector3(9, 0, 0),
            teleport: teleport, emote: teleport ? null : new EmoteState("wave", StartSeq: 4, StartTick: 40)));

        simulation.SimulateTick(peers, 1);
        AssertAcceptedInterestMessage(previouslyVisible, 3, 2);

        simulation.SimulateTick(peers, 2);
        OutgoingMessage message = DrainSingleMessage();
        Assert.Multiple(() =>
        {
            Assert.That(message.Message.MessageCase, Is.EqualTo(teleport
                ? ServerMessage.MessageOneofCase.Teleported
                : ServerMessage.MessageOneofCase.EmoteStarted));
            Assert.That(teleport ? message.Message.Teleported.Sequence : message.Message.EmoteStarted.Sequence, Is.EqualTo(4u));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSnapshot_AcceptedEventEvictedAfterCollection_UsesCapturedEvent(bool teleport)
    {
        PrepareCapturedInterest(previouslyVisible: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0), teleport: teleport,
            emote: teleport ? null : new EmoteState("wave", StartSeq: 3, StartTick: 30));
        InterleaveAfterSpatialCollection(() =>
        {
            for (uint seq = 4; seq <= 4 + RING_CAPACITY; seq++)
                PublishInterestSnapshot(seq, "old", new Vector3(9, 0, 0),
                    emote: new EmoteState(null, StartSeq: 3, StartTick: 30, StopReason: EmoteStopReason.Cancelled));
            Assert.That(snapshotBoard.TryRead(subject, 3, out _), Is.False);
        });

        simulation.SimulateTick(peers, 1);

        OutgoingMessage message = DrainSingleMessage();
        Assert.Multiple(() =>
        {
            Assert.That(message.Message.MessageCase, Is.EqualTo(teleport
                ? ServerMessage.MessageOneofCase.Teleported
                : ServerMessage.MessageOneofCase.EmoteStarted));
            Assert.That(teleport ? message.Message.Teleported.Sequence : message.Message.EmoteStarted.Sequence, Is.EqualTo(3u));
            Assert.That(teleport ? message.Message.Teleported.State.PositionXQuantized
                : message.Message.EmoteStarted.PlayerState.PositionXQuantized,
                Is.EqualTo(2).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Seq, Is.EqualTo(3u));
        });
    }

    [Test]
    public void InterestSnapshot_AcceptedStopEvictedAfterCollection_PreservesCapturedReason()
    {
        PrepareCapturedInterest(previouslyVisible: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0),
            emote: new EmoteState("wave", StartSeq: 3, StartTick: 30));
        simulation.SimulateTick(peers, 1);
        Assert.That(DrainSingleMessage().Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));

        PublishInterestSnapshot(4, "old", new Vector3(2, 0, 0),
            emote: new EmoteState(null, StartSeq: 3, StartTick: 30, StopReason: EmoteStopReason.Completed));
        InterleaveAfterSpatialCollection(() =>
        {
            for (uint seq = 5; seq <= 5 + RING_CAPACITY; seq++)
                PublishInterestSnapshot(seq, "old", new Vector3(9, 0, 0),
                    emote: new EmoteState(null, StartSeq: 3, StartTick: 30, StopReason: EmoteStopReason.Cancelled));
            Assert.That(snapshotBoard.TryRead(subject, 4, out _), Is.False);
        });

        simulation.SimulateTick(peers, 2);

        EmoteStopped stopped = DrainSingleMessage().Message.EmoteStopped;
        Assert.Multiple(() =>
        {
            Assert.That(stopped.Sequence, Is.EqualTo(4u));
            Assert.That(stopped.Reason, Is.EqualTo(EmoteStopReason.Completed));
            Assert.That(stopped.PlayerState.PositionXQuantized, Is.EqualTo(2).Within(PlayerState.PositionXQuantizedStep));
        });
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InterestSnapshot_ListenerEligibilityChangesAfterCollection_DeliversAcceptedParcel(bool previouslyVisible, bool realmChange)
    {
        PrepareCapturedInterest(previouslyVisible, sceneListener: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => PublishInterestSnapshot(4, realmChange ? "new" : "old",
            new Vector3(9, 0, 0), teleport: true, parcel: realmChange ? 0 : 5));

        simulation.SimulateTick(peers, 1);

        AssertAcceptedInterestMessage(previouslyVisible, 3, 2);
        Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Parcel, Is.Zero);
        Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Realm, Is.EqualTo("old"));
        AssertAcceptedViewSweptOnce(1);
    }

    [Test]
    public void InterestSnapshot_ResyncBaselinePublishedAfterCollection_ReturnsCapturedFullState()
    {
        PrepareCapturedInterest(previouslyVisible: true, resyncWithDelta: true);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() => PublishInterestSnapshot(4, "old", new Vector3(9, 0, 0)));
        AddResyncRequest(observer, subject, knownSeq: 4);

        simulation.SimulateTick(peers, 1);

        OutgoingMessage message = DrainSingleMessage();
        Assert.Multiple(() =>
        {
            Assert.That(message.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
            Assert.That(message.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateFull));
            Assert.That(message.Message.PlayerStateFull.Sequence, Is.EqualTo(3u));
            Assert.That(message.Message.PlayerStateFull.State.PositionXQuantized,
                Is.EqualTo(2).Within(PlayerState.PositionXQuantizedStep));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InterestSnapshot_DisconnectAfterCollection_VetoesDeliveryBeforeNormalRetirement(bool previouslyVisible)
    {
        PrepareCapturedInterest(previouslyVisible);
        PublishInterestSnapshot(3, "old", new Vector3(2, 0, 0));
        InterleaveAfterSpatialCollection(() =>
        {
            snapshotBoard.ClearActive(subject);
            realmGrids.Remove(subject);
        });

        simulation.SimulateTick(peers, 1);

        Assert.That(DrainAllMessages(), Is.Empty);
        if (previouslyVisible)
        {
            Assert.That(simulation.observerViews[observer][subject].LastSentWalletId, Is.EqualTo("0xSUBJECT_WALLET"));
            Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.Zero);
            AssertAcceptedViewSweptOnce(0);
        }
        else
            Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
    }

    [Test]
    public void InterestSnapshot_FinalCleanupAfterCollection_DoesNotAnnounceMissingIdentity()
    {
        PrepareCapturedInterest(previouslyVisible: false);
        InterleaveAfterSpatialCollection(() =>
        {
            snapshotBoard.ClearActive(subject);
            realmGrids.Remove(subject);
            identityBoard.Remove(subject);
            profileBoard.Remove(subject);
        });

        simulation.SimulateTick(peers, 1);

        Assert.That(DrainAllMessages(), Is.Empty);
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InterestSnapshot_RecycledSlotAfterCollection_DiscardsOldCaptureAndReseedsNextQuery(bool previouslyVisible, bool sameWallet)
    {
        PrepareCapturedInterest(previouslyVisible);
        string wallet = sameWallet ? "0xSUBJECT_WALLET" : "0xREPLACEMENT_WALLET";
        InterleaveAfterSpatialCollection(() =>
        {
            snapshotBoard.ClearActive(subject);
            realmGrids.Remove(subject);
            identityBoard.Remove(subject);
            profileBoard.Remove(subject);
            identityBoard.Set(subject, wallet);
            snapshotBoard.SetActive(subject);
            PublishInterestSnapshot(1, "old", new Vector3(9, 0, 0));
        });

        simulation.SimulateTick(peers, 1);
        Assert.That(DrainAllMessages(), Is.Empty, "A query accepted under the old registration must be discarded");

        simulation.SimulateTick(peers, 2);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(message => message.Message.MessageCase), Is.EqualTo(previouslyVisible
            ? new[] { ServerMessage.MessageOneofCase.PlayerLeft, ServerMessage.MessageOneofCase.PlayerJoined }
            : new[] { ServerMessage.MessageOneofCase.PlayerJoined }));
        PlayerJoined joined = messages[^1].Message.PlayerJoined;
        Assert.Multiple(() =>
        {
            Assert.That(joined.UserId, Is.EqualTo(wallet));
            Assert.That(joined.State.Sequence, Is.EqualTo(1u));
            Assert.That(joined.State.State.PositionXQuantized, Is.EqualTo(9).Within(PlayerState.PositionXQuantizedStep));
        });
    }

    private void PrepareCapturedInterest(bool previouslyVisible, bool resyncWithDelta = false, bool sceneListener = false)
    {
        UseSpatialInterest();
        IAreaOfInterest spatial = areaOfInterest;
        interestCollectionInterleave = null;
        areaOfInterest = Substitute.For<IAreaOfInterest>();
        areaOfInterest.When(aoi => aoi.GetVisibleSubjects(Arg.Any<PeerIndex>(), Arg.Any<PeerSnapshot>(), Arg.Any<IInterestCollector>()))
            .Do(call =>
            {
                spatial.GetVisibleSubjects(call.ArgAt<PeerIndex>(0), call.ArgAt<PeerSnapshot>(1), call.ArgAt<IInterestCollector>(2));
                RunInterestInterleave();
            });
        areaOfInterest.When(aoi => aoi.GetVisibleSubjects(Arg.Any<PeerIndex>(), Arg.Any<SceneListenerState>(), Arg.Any<IInterestCollector>()))
            .Do(call =>
            {
                spatial.GetVisibleSubjects(call.ArgAt<PeerIndex>(0), call.ArgAt<SceneListenerState>(1), call.ArgAt<IInterestCollector>(2));
                RunInterestInterleave();
            });
        simulation = new PeerSimulation(areaOfInterest, snapshotBoard, realmGrids, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(), profileBoard, peerIndexAllocator,
            Substitute.For<ILogger<PeerSimulation>>(), resyncWithDelta: resyncWithDelta);
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        if (sceneListener)
            MakeSceneListener(observer, "old", parcels: [0]);
        if (!previouslyVisible)
            return;

        simulation.SimulateTick(peers, 0);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));
    }

    private void PublishInterestSnapshot(uint seq, string realm, Vector3 position, bool teleport = false,
        EmoteState? emote = null, Vector3? globalPosition = null, int parcel = 0)
    {
        realmGrids.Remove(subject);
        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: seq, serverTick: seq * 10, realm: realm, parcel: parcel,
            position: position, globalPosition: globalPosition, isTeleport: teleport, emote: emote));
        realmGrids.Set(subject, realm, globalPosition ?? position);
    }

    private void InterleaveAfterSpatialCollection(Action interleave) => interestCollectionInterleave = interleave;

    private void RunInterestInterleave()
    {
        Action? interleave = interestCollectionInterleave;
        interestCollectionInterleave = null;
        interleave?.Invoke();
    }

    private void AssertAcceptedInterestMessage(bool previouslyVisible, uint sequence, float positionX)
    {
        OutgoingMessage message = DrainSingleMessage();
        Assert.That(message.Message.MessageCase, Is.EqualTo(previouslyVisible
            ? ServerMessage.MessageOneofCase.PlayerStateDelta
            : ServerMessage.MessageOneofCase.PlayerJoined));
        Assert.That(previouslyVisible ? message.Message.PlayerStateDelta.NewSeq : message.Message.PlayerJoined.State.Sequence,
            Is.EqualTo(sequence));
        if (previouslyVisible)
            Assert.That(message.Message.PlayerStateDelta.PositionXQuantized,
                Is.EqualTo(positionX).Within(PlayerState.PositionXQuantizedStep));
        else
        {
            Assert.That(message.Message.PlayerJoined.Realm, Is.EqualTo("old"));
            Assert.That(message.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
            Assert.That(message.Message.PlayerJoined.State.State.PositionXQuantized,
                Is.EqualTo(positionX).Within(PlayerState.PositionXQuantizedStep));
        }
    }

    private void AssertAcceptedViewSweptOnce(uint acceptedTick)
    {
        for (uint tick = acceptedTick + 1; tick <= FirstSweepTickAfter(acceptedTick); tick++)
            simulation.SimulateTick(peers, tick);

        OutgoingMessage message = DrainSingleMessage();
        Assert.That(message.Message.PlayerLeft.SubjectId, Is.EqualTo(subject.Value));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
        simulation.SimulateTick(peers, FirstSweepTickAfter(acceptedTick) + SWEEP_CHECK_INTERVAL);
        Assert.That(DrainAllMessages(), Is.Empty);
    }
}
