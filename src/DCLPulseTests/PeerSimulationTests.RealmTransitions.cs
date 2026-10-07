using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    /// <summary>Gap whose closing tick is the sweep at which a tick-0 view is exactly VIEW_STALE_TICKS old.</summary>
    private const int GAP_TO_STALE_BOUNDARY = (int)VIEW_STALE_TICKS - 1;

    /// <summary>Gap that spans the first sweep evicting a tick-0 view.</summary>
    private const int GAP_PAST_FIRST_SWEEP = (int)(VIEW_STALE_TICKS + SWEEP_CHECK_INTERVAL) + 1;

    private PeerSimulation CreateSimulation(IAreaOfInterest aoi, bool selfMirrorEnabled = false,
        ILogger<PeerSimulation>? logger = null) =>
        new (aoi, snapshotBoard, realmGrids, identityBoard, messagePipe, SimulationSteps, timeProvider,
            Substitute.For<ITransport>(), profileBoard, peerIndexAllocator,
            logger ?? Substitute.For<ILogger<PeerSimulation>>(), selfMirrorEnabled);

    private void UseSpatialInterest()
    {
        areaOfInterest = new SpatialHashAreaOfInterest(realmGrids, snapshotBoard,
            Options.Create(new SpatialHashAreaOfInterestOptions()));
        simulation = CreateSimulation(areaOfInterest);
    }

    private void PlaceInRealm(PeerIndex peer, string realm, uint seq, bool teleport = false, EmoteState? emote = null)
    {
        if (teleport)
        {
            var parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
            var transport = Substitute.For<ITransport>();
            var handler = new TeleportHandler(
                Substitute.For<ILogger<TeleportHandler>>(), snapshotBoard,
                new PeerSnapshotPublisher(snapshotBoard, realmGrids, parcelEncoder, timeProvider),
                new DiscreteEventRateLimiter(
                    Options.Create(new DiscreteEventRateLimiterOptions { RatePerSecond = 0 }), timeProvider, transport),
                new FieldValidator(
                    Options.Create(new FieldValidatorOptions()), Options.Create(new SceneListenerOptions()),
                    parcelEncoder, SceneListenerTestFactory.CellMapper(), SceneListenerTestFactory.Limiter(), transport));
            var handlerPeers = new Dictionary<PeerIndex, PeerState>
            {
                [peer] = peers.TryGetValue(peer, out PeerState? state)
                    ? state
                    : new PeerState(PeerConnectionState.AUTHENTICATED),
            };
            timeProvider.MonotonicTime.Returns(seq * 10);
            handler.Handle(handlerPeers, peer, new ClientMessage
            {
                Teleport = new TeleportRequest { Realm = realm, ParcelIndex = parcelEncoder.Encode(0, 0) },
            });
            Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
            Assert.That(snapshot.Seq, Is.EqualTo(seq));
            Assert.That(snapshot.Realm, Is.EqualTo(realm));
            if (emote != null)
                snapshotBoard.Publish(peer, snapshot with { Emote = emote });
            return;
        }

        snapshotBoard.Publish(peer, TestSnapshots.Make(seq: seq, serverTick: seq * 10,
            realm: realm, emote: emote));
        realmGrids.Set(peer, realm, Vector3.Zero);
    }

    [TestCase(true, 0)]
    [TestCase(true, 20)]
    [TestCase(true, GAP_TO_STALE_BOUNDARY)]
    [TestCase(true, GAP_PAST_FIRST_SWEEP)]
    [TestCase(false, 0)]
    [TestCase(false, 20)]
    [TestCase(false, GAP_TO_STALE_BOUNDARY)]
    [TestCase(false, GAP_PAST_FIRST_SWEEP)]
    public void RealmTransition_Coteleport_ReseedsIdentityBeforeFurtherMovement(bool observerFirst, int gapTicks)
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));

        PlaceInRealm(observerFirst ? observer : subject, "new", 3, teleport: true);
        var received = new List<(uint Tick, OutgoingMessage Message)>();
        for (uint tick = 1; tick <= gapTicks; tick++)
        {
            simulation.SimulateTick(peers, tick);
            foreach (OutgoingMessage message in DrainAllMessages()) received.Add((tick, message));
        }

        uint closingTick = (uint)gapTicks + 1;
        PlaceInRealm(observerFirst ? subject : observer, "new", 3, teleport: true);
        simulation.SimulateTick(peers, closingTick);
        foreach (OutgoingMessage message in DrainAllMessages()) received.Add((closingTick, message));

        // An observer teleport retires the view at once; a subject teleport leaves it to the
        // stale sweep, or to the observer's own realm change if that comes first.
        uint leftTick = observerFirst ? 1u : Math.Min(FirstSweepTickAfter(0), closingTick);
        Assert.That(received.Select(r => (r.Tick, r.Message.Message.MessageCase)), Is.EqualTo(new[]
        {
            (leftTick, ServerMessage.MessageOneofCase.PlayerLeft),
            (closingTick, ServerMessage.MessageOneofCase.PlayerJoined),
        }));
        OutgoingMessage joined = received[1].Message;
        Assert.Multiple(() =>
        {
            Assert.That(received[0].Message.Message.PlayerLeft.SubjectId, Is.EqualTo(subject.Value));
            Assert.That(joined.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
            Assert.That(joined.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
            Assert.That(joined.Message.PlayerJoined.Realm, Is.EqualTo("new"));
            Assert.That(joined.Message.PlayerJoined.State.Sequence, Is.EqualTo(3u));
        });

        // The reseeded baseline must support continued movement, not repeated joins or
        // deltas referencing the old realm's baseline. Run beyond the stale-view sweep.
        for (uint tick = (uint)gapTicks + 2, seq = 4; seq < 104; tick++, seq++)
        {
            PlaceInRealm(subject, "new", seq);
            simulation.SimulateTick(peers, tick);
            OutgoingMessage movement = DrainSingleMessage();
            Assert.That(movement.Message.PlayerStateDelta, Is.Not.Null);
            Assert.That(movement.Message.PlayerStateDelta.BaselineSeq, Is.EqualTo(seq - 1));
            Assert.That(movement.Message.PlayerStateDelta.NewSeq, Is.EqualTo(seq));
        }
    }

    [Test]
    public void RealmTransition_ObserverReturnsToUnchangedSubject_ReseedsIdentity()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        PlaceInRealm(observer, "away", 3, teleport: true);
        simulation.SimulateTick(peers, 1);
        Assert.That(DrainSingleMessage().Message.PlayerLeft.SubjectId, Is.EqualTo(subject.Value));
        PlaceInRealm(observer, "old", 4, teleport: true);
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));
    }

    [Test]
    public void RealmTransition_SameRealmTeleport_KeepsIdentity()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        PlaceInRealm(observer, "old", 3, teleport: true);
        PlaceInRealm(subject, "old", 3, teleport: true);
        simulation.SimulateTick(peers, 1);
        Assert.That(DrainSingleMessage().Message.Teleported.Realm, Is.EqualTo("old"));
    }

    [Test]
    public void RealmTransition_ReseedsCurrentProfileAndActiveEmote_WithoutAnsweringOldResync()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        profileBoard.Set(subject, 7);
        AddResyncRequest(observer, subject, 2);
        PlaceInRealm(observer, "new", 3, teleport: true);
        PlaceInRealm(subject, "new", 3, teleport: true, emote: new EmoteState("wave", StartSeq: 3, StartTick: 30));
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
            ServerMessage.MessageOneofCase.EmoteStarted,
        }));
        Assert.That(messages[1].Message.PlayerJoined.ProfileVersion, Is.EqualTo(7));
        Assert.That(messages[2].Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty, "Reseeding must not replay the same emote or profile every tick");
    }

    [Test]
    public void RealmTransition_RetainedSubjectChangesRealm_ReseedsForMultiRealmListener()
    {
        UseSpatialInterest();
        int destinationParcel = new ParcelEncoder(Options.Create(new ParcelEncoderOptions())).Encode(0, 0);
        MakeSceneListener(observer, new Dictionary<string, int[]> { ["old"] = [0], ["new"] = [destinationParcel] });
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        PlaceInRealm(subject, "new", 3, teleport: true);
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
        }));
        Assert.That(messages[1].Message.PlayerJoined.Realm, Is.EqualTo("new"));
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, RING_CAPACITY * 2)]
    [TestCase(true, RING_CAPACITY * 2)]
    public void RealmTransition_BatchedRoundTrip_RestoresClientIdentityAndCancelsRemoval(bool drainPurgeWhileAway, int trailingSnapshots)
    {
        UseSpatialInterest();
        var client = new RealmTransitionClient();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);

        client.Teleport("away");
        PlaceInRealm(observer, "away", 3, teleport: true);
        if (drainPurgeWhileAway)
            client.Receive(new ServerMessage { PlayerStateDelta = new PlayerStateDeltaTier0 { SubjectId = subject.Value } });
        client.Teleport("old");
        PlaceInRealm(observer, "old", 4, teleport: true);

        // Every transition can be evicted before the simulation runs; latest realm text
        // and scans of the bounded ring both miss the round trip in that case.
        for (uint seq = 5; seq < 5 + trailingSnapshots; seq++)
            snapshotBoard.Publish(observer, TestSnapshots.Make(seq: seq));
        if (trailingSnapshots > RING_CAPACITY)
        {
            Assert.That(snapshotBoard.TryRead(observer, 3, out _), Is.False);
            Assert.That(snapshotBoard.TryRead(observer, 4, out _), Is.False);
        }

        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
        }));
        Assert.That(messages.All(m => m.PacketMode == PacketMode.RELIABLE), Is.True);
        foreach (OutgoingMessage message in messages) client.Receive(message.Message);
        Assert.That(client.Knows(subject.Value), Is.True);
        Assert.That(client.PendingRemovals, Is.Empty);

        for (uint tick = 2; tick < 102; tick++)
        {
            PlaceInRealm(subject, "old", tick + 1);
            simulation.SimulateTick(peers, tick);
            foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);
        }
        Assert.That(client.AcceptedDeltas, Is.EqualTo(100));
        Assert.That(client.PendingRemovals, Is.Empty);
    }

    [TestCase(0)]
    [TestCase(RING_CAPACITY * 2)]
    public void RealmTransition_SubjectRoundTrip_KeepsRetainedIdentity(int trailingSnapshots)
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        PlaceInRealm(subject, "away", 3, teleport: true);
        PlaceInRealm(subject, "old", 4, teleport: true);
        for (uint seq = 5; seq < 5 + trailingSnapshots; seq++)
            snapshotBoard.Publish(subject, TestSnapshots.Make(seq: seq));
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            trailingSnapshots == 0
                ? ServerMessage.MessageOneofCase.Teleported
                : ServerMessage.MessageOneofCase.PlayerStateDelta,
        }));
        Assert.That(simulation.observerViews[observer][subject].LastSentWalletId, Is.EqualTo("0xSUBJECT_WALLET"));
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    [TestCase(0)]
    [TestCase(RING_CAPACITY * 2)]
    public void RealmTransition_SubjectRoundTrip_ForeignEmoteStartUsesDestinationPose(int foreignCarrySnapshots)
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();

        PlaceInRealm(subject, "away", 3, teleport: true);
        PublishEmoteSnapshot(subject, seq: 4, emoteId: "wave", startTick: 40, position: new Vector3(1, 0, 0));
        for (uint seq = 5; seq < 5 + foreignCarrySnapshots; seq++)
            PublishSnapshot(subject, seq, position: new Vector3(2, 0, 0));
        uint returnSeq = (uint)(5 + foreignCarrySnapshots);
        PlaceInRealm(subject, "old", returnSeq, teleport: true);

        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.Teleported,
            ServerMessage.MessageOneofCase.EmoteStarted,
        }));
        Assert.Multiple(() =>
        {
            Assert.That(messages[0].Message.Teleported.Realm, Is.EqualTo("old"));
            Assert.That(messages[1].Message.EmoteStarted.Sequence, Is.EqualTo(returnSeq));
            Assert.That(messages[1].Message.EmoteStarted.PlayerState.PositionXQuantized,
                Is.EqualTo(0).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(messages[1].Message.EmoteStarted.ServerTick, Is.EqualTo(40u));
            Assert.That(messages[1].Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
            Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Realm, Is.EqualTo("old"));
        });
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
        PlaceInRealm(subject, "old", returnSeq + 1);
        simulation.SimulateTick(peers, 3);
        Assert.That(DrainSingleMessage().Message.PlayerStateDelta.BaselineSeq, Is.EqualTo(returnSeq));
    }

    [Test]
    public void RealmTransition_SubjectRoundTrip_ForeignEmoteStopUsesDestinationPose()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2, emote: new EmoteState("wave", StartSeq: 2, StartTick: 20));
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();

        PlaceInRealm(subject, "away", 3, teleport: true);
        PublishEmoteStopSnapshot(subject, seq: 4, emoteStartTick: 20,
            reason: EmoteStopReason.Cancelled, position: new Vector3(1, 0, 0));
        PlaceInRealm(subject, "old", 5, teleport: true);

        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.Teleported,
            ServerMessage.MessageOneofCase.EmoteStopped,
        }));
        Assert.Multiple(() =>
        {
            Assert.That(messages[1].Message.EmoteStopped.Sequence, Is.EqualTo(5u));
            Assert.That(messages[1].Message.EmoteStopped.PlayerState.PositionXQuantized,
                Is.EqualTo(0).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(messages[1].Message.EmoteStopped.Reason, Is.EqualTo(EmoteStopReason.Cancelled));
            Assert.That(simulation.observerViews[observer][subject].LastSentEmote, Is.Null);
        });
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    [Test]
    public void RealmTransition_SubjectRoundTrip_EvictedForeignEmoteStopReconcilesIdleLedger()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2, emote: new EmoteState("wave", StartSeq: 2, StartTick: 20));
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();

        PlaceInRealm(subject, "away", 3, teleport: true);
        PublishEmoteStopSnapshot(subject, seq: 4, emoteStartTick: 20);
        for (uint seq = 5; seq < 5 + RING_CAPACITY * 2; seq++)
            PublishSnapshot(subject, seq, position: new Vector3(1, 0, 0));
        uint returnSeq = 5 + RING_CAPACITY * 2;
        PlaceInRealm(subject, "old", returnSeq, teleport: true);
        Assert.That(snapshotBoard.TryRead(subject, 4, out _), Is.False);

        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.Teleported,
            ServerMessage.MessageOneofCase.EmoteStopped,
        }));
        Assert.Multiple(() =>
        {
            Assert.That(messages[1].Message.EmoteStopped.Sequence, Is.EqualTo(returnSeq));
            Assert.That(messages[1].Message.EmoteStopped.PlayerState.PositionXQuantized,
                Is.EqualTo(0).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(simulation.observerViews[observer][subject].LastSentEmote, Is.Null);
        });
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
        PlaceInRealm(subject, "old", returnSeq + 1);
        simulation.SimulateTick(peers, 3);
        Assert.That(DrainSingleMessage().Message.PlayerStateDelta.BaselineSeq, Is.EqualTo(returnSeq));
    }

    [TestCase(0)]
    [TestCase(RING_CAPACITY * 2)]
    public void RealmTransition_SubjectRoundTrip_ForeignStopKeepsExplorerBaselineIdle(int foreignCarrySnapshots)
    {
        UseSpatialInterest();
        ILogger<PeerSimulation> simulationLogger = Substitute.For<ILogger<PeerSimulation>>();
        simulation = CreateSimulation(areaOfInterest, logger: simulationLogger);
        var client = new EmoteBaselineClient();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2, emote: new EmoteState("wave", StartSeq: 2, StartTick: 20));
        simulation.SimulateTick(peers, 0);
        foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);
        Assert.That(client.IsEmoting, Is.True);

        PlaceInRealm(subject, "away", 3, teleport: true);
        PublishEmoteStopSnapshot(subject, seq: 4, emoteStartTick: 20);
        for (uint seq = 5; seq < 5 + foreignCarrySnapshots; seq++)
            PublishSnapshot(subject, seq, position: new Vector3(1, 0, 0));
        uint returnSeq = (uint)(5 + foreignCarrySnapshots);
        PlaceInRealm(subject, "old", returnSeq, teleport: true);

        simulation.SimulateTick(peers, 1);
        foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);
        Assert.That(client.IsEmoting, Is.False, "An equal-sequence stop must not leave the teleport baseline marked emoting");

        PlaceInRealm(subject, "old", returnSeq + 1);
        simulation.SimulateTick(peers, 2);
        foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);
        Assert.That(client.Sequence, Is.EqualTo(returnSeq + 1));
        Assert.That(client.IsEmoting, Is.False, "Explorer's delta merge preserves the baseline's emote flag");
        Assert.That(simulationLogger.ReceivedCalls().Any(call =>
            call.GetMethodInfo().Name == nameof(ILogger.Log) && call.GetArguments()[0] is LogLevel.Error), Is.False,
            "Sharing the latest pose between a stop and teleport is intentional");
    }

    [Test]
    public void RealmTransition_SubjectRoundTrip_StopInOriginalRealmKeepsExplorerBaselineIdle()
    {
        UseSpatialInterest();
        var client = new EmoteBaselineClient();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2, emote: new EmoteState("wave", StartSeq: 2, StartTick: 20));
        simulation.SimulateTick(peers, 0);
        foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);

        PublishEmoteStopSnapshot(subject, seq: 3, emoteStartTick: 20, position: new Vector3(1, 0, 0));
        PlaceInRealm(subject, "away", 4, teleport: true);
        PlaceInRealm(subject, "old", 5, teleport: true);
        simulation.SimulateTick(peers, 1);
        foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);
        Assert.That(client.IsEmoting, Is.False, "An older stop must clear the emote flag on the newer teleport baseline");

        PlaceInRealm(subject, "old", 6);
        simulation.SimulateTick(peers, 2);
        foreach (OutgoingMessage message in DrainAllMessages()) client.Receive(message.Message);
        Assert.That(client.Sequence, Is.EqualTo(6u));
        Assert.That(client.IsEmoting, Is.False);
    }

    [Test]
    public void RealmTransition_SubjectRoundTrip_ForeignStopThenNewStartAnnouncesOnlyCurrentEmote()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2, emote: new EmoteState("wave", StartSeq: 2, StartTick: 20));
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();

        PlaceInRealm(subject, "away", 3, teleport: true);
        PublishEmoteStopSnapshot(subject, seq: 4, emoteStartTick: 20);
        PublishEmoteSnapshot(subject, seq: 5, emoteId: "clap", startTick: 50, position: new Vector3(1, 0, 0));
        PlaceInRealm(subject, "old", 6, teleport: true);

        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.Teleported,
            ServerMessage.MessageOneofCase.EmoteStarted,
        }));
        Assert.Multiple(() =>
        {
            Assert.That(messages[1].Message.EmoteStarted.Sequence, Is.EqualTo(6u));
            Assert.That(messages[1].Message.EmoteStarted.PlayerState.PositionXQuantized,
                Is.EqualTo(0).Within(PlayerState.PositionXQuantizedStep));
            Assert.That(messages[1].Message.EmoteStarted.EmoteId, Is.EqualTo("clap"));
            Assert.That(messages[1].Message.EmoteStarted.ServerTick, Is.EqualTo(50u));
        });
    }

    [Test]
    public void RealmTransition_ObserverRoundTripFollowedBySameRealmTeleport_ReseedsOnce()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();

        PlaceInRealm(observer, "away", 3, teleport: true);
        PlaceInRealm(observer, "old", 4, teleport: true);
        PlaceInRealm(observer, "old", 5, teleport: true);
        for (uint seq = 6; seq <= RING_CAPACITY * 3; seq++)
            snapshotBoard.Publish(observer, TestSnapshots.Make(seq: seq));
        Assert.That(snapshotBoard.TryRead(observer, 3, out _), Is.False);
        Assert.That(snapshotBoard.TryRead(observer, 5, out _), Is.False);

        simulation.SimulateTick(peers, 1);
        Assert.That(DrainAllMessages().Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
        }));
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    [Test]
    public void RealmTransition_SelfMirror_ReseedsAndKeepsItsViewStamped()
    {
        UseSpatialInterest();
        simulation = CreateSimulation(areaOfInterest, selfMirrorEnabled: true);
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
    public void RealmTransition_InheritedEmoteSurvivesRealmChangeAndHistoryEviction()
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
    public void RealmTransition_ObserverCleanupAndSlotReuse_DiscardsPendingInvalidation()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        PlaceInRealm(observer, "away", 3, teleport: true);
        Assert.That(peers[observer].ObserverViewsInvalidated, Is.True);
        peers[observer].ConnectionState = PeerConnectionState.DISCONNECTING;
        peers[observer].TransportState = new PeerTransportState(ConnectionTime: 0, DisconnectionTime: 0);
        timeProvider.MonotonicTime.Returns(6000u);
        simulation.SimulateTick(peers, 1);
        Assert.That(peers, Does.Not.ContainKey(observer));
        Assert.That(simulation.observerViews, Does.Not.ContainKey(observer));

        peers[observer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        Assert.That(peers[observer].ObserverViewsInvalidated, Is.False);
        snapshotBoard.SetActive(observer);
        PlaceInRealm(observer, "new", 1);
        PlaceInRealm(subject, "new", 3, teleport: true);
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("new"));
        simulation.SimulateTick(peers, 3);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    /// <summary>
    ///     Models the fixed Explorer cache: stops clear the emote flag regardless of the
    ///     movement sequence, while movement baselines never go backwards. Deltas preserve
    ///     the cached flag and teleports read the active emote set at receipt.
    /// </summary>
    private sealed class EmoteBaselineClient
    {
        private bool activeEmote;
        public uint Sequence { get; private set; }
        public bool IsEmoting { get; private set; }

        public void Receive(ServerMessage message)
        {
            switch (message.MessageCase)
            {
                case ServerMessage.MessageOneofCase.PlayerJoined:
                    Update(message.PlayerJoined.State.Sequence);
                    break;
                case ServerMessage.MessageOneofCase.Teleported:
                    Update(message.Teleported.Sequence);
                    break;
                case ServerMessage.MessageOneofCase.EmoteStarted:
                    activeEmote = true;
                    Update(message.EmoteStarted.Sequence, allowOverrides: true);
                    break;
                case ServerMessage.MessageOneofCase.EmoteStopped:
                    activeEmote = false;
                    Update(message.EmoteStopped.Sequence, allowOverrides: true);
                    IsEmoting = false;
                    break;
                case ServerMessage.MessageOneofCase.PlayerStateDelta:
                    Assert.That(message.PlayerStateDelta.BaselineSeq, Is.EqualTo(Sequence));
                    Sequence = message.PlayerStateDelta.NewSeq;
                    break;
                default:
                    Assert.Fail($"Unexpected message {message.MessageCase}");
                    break;
            }
        }

        private void Update(uint sequence, bool allowOverrides = false)
        {
            if (sequence > Sequence || (allowOverrides && sequence == Sequence))
            {
                Sequence = sequence;
                IsEmoting = activeEmote;
            }
        }
    }

    /// <summary>
    ///     Small protocol consumer modeling Explorer's relevant realm lifecycle: local teleports
    ///     queue removals immediately, routing identities purge before the next message, and a
    ///     same-realm join restores identity and cancels removal. This is deliberately a model,
    ///     not Unity rendering; assertions also check the exact server message order independently.
    /// </summary>
    private sealed class RealmTransitionClient
    {
        private readonly Dictionary<uint, (string Wallet, string Realm, uint Sequence)> identities = new ();
        private string realm = "old";
        private bool purgeRequested;
        public HashSet<string> PendingRemovals { get; } = new ();
        public int AcceptedDeltas { get; private set; }

        public bool Knows(uint subjectId) => identities.ContainsKey(subjectId);

        public void Teleport(string destination)
        {
            realm = destination;
            foreach (var identity in identities.Values)
                if (identity.Realm != realm) PendingRemovals.Add(identity.Wallet);
            purgeRequested = true;
        }

        public void Receive(ServerMessage message)
        {
            if (purgeRequested)
            {
                foreach (uint id in identities.Where(pair => pair.Value.Realm != realm).Select(pair => pair.Key).ToArray())
                    identities.Remove(id);
                purgeRequested = false;
            }

            if (message.PlayerJoined is { } join && join.Realm == realm)
            {
                PendingRemovals.Remove(join.UserId);
                identities[join.State.SubjectId] = (join.UserId, join.Realm, join.State.Sequence);
            }
            else if (message.PlayerLeft is { } left && identities.Remove(left.SubjectId, out var departing))
                PendingRemovals.Add(departing.Wallet);
            else if (message.PlayerStateDelta is { } delta && identities.TryGetValue(delta.SubjectId, out var identity)
                     && identity.Realm == realm && identity.Sequence == delta.BaselineSeq)
            {
                identities[delta.SubjectId] = (identity.Wallet, identity.Realm, delta.NewSeq);
                AcceptedDeltas++;
            }
        }
    }
}
