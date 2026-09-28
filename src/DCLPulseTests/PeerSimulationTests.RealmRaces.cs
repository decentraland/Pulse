using Decentraland.Pulse;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Peers;
using System.Numerics;
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
        simulation = CreateSimulation(areaOfInterest);
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

        // The teleport completes: the subject leaves the announced realm's grid.
        realmGrids.Set(subject, "new", Vector3.Zero);
        for (uint tick = 2; tick <= FirstSweepTickAfter(1); tick++)
            simulation.SimulateTick(peers, tick);
        Assert.That(DrainAllMessages(), Is.Empty, "Removed views must not emit another leave in the stale sweep");
    }

    [Test]
    public void RealmRace_SubjectMovesToAnotherListenerRealm_IsNotAnnouncedOutsideThatRealmsParcels()
    {
        MakeSceneListener(observer, new Dictionary<string, int[]> { ["old"] = [0], ["new"] = [5] });
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));

        // Collected through the old realm's grid, whose parcel 0 is announced, while the latest
        // snapshot names parcel 0 of a realm that announces only parcel 5.
        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 3, serverTick: 30, realm: "new", isTeleport: true));
        simulation.SimulateTick(peers, 1);
        Assert.That(DrainAllMessages().Select(message => message.Message.MessageCase),
            Is.EqualTo(new[] { ServerMessage.MessageOneofCase.PlayerLeft }));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
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
    public void RealmRace_TierDelayedRoundTrip_RetiresAtOnceAndRejoinsOnTheNextDueTick()
    {
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_2));
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));

        PlaceInRealm(subject, "away", 3, teleport: true);
        PlaceInRealm(subject, "old", 4, teleport: true);
        var received = new List<(uint Tick, ServerMessage.MessageOneofCase Case)>();
        for (uint tick = 1; tick <= 4; tick++)
        {
            simulation.SimulateTick(peers, tick);
            foreach (OutgoingMessage message in DrainAllMessages())
                received.Add((tick, message.Message.MessageCase));
        }

        Assert.That(received, Is.EqualTo(new[]
        {
            (1u, ServerMessage.MessageOneofCase.PlayerLeft),
            (4u, ServerMessage.MessageOneofCase.PlayerJoined),
        }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealmRace_TierDelayedSubjectTeleports_RetiresOnTheSameTick(bool pendingResync)
    {
        bool collectSubject = true;
        bool teleportAfterCollection = false;
        IAreaOfInterest aoi = Substitute.For<IAreaOfInterest>();
        aoi.When(x => x.GetVisibleSubjects(Arg.Any<PeerIndex>(), Arg.Any<PeerSnapshot>(), Arg.Any<IInterestCollector>()))
           .Do(call =>
            {
                if (collectSubject)
                    call.ArgAt<IInterestCollector>(2).Add(subject, PeerViewSimulationTier.TIER_2);

                if (!teleportAfterCollection)
                    return;

                // Collection saw the old realm's grid once; later collections never return the subject.
                PlaceInRealm(subject, "other", 3, teleport: true);
                teleportAfterCollection = false;
                collectSubject = false;
            });
        simulation = CreateSimulation(aoi);
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));

        // Tick 1 is not due for TIER_2; the retirement must not wait for a due tick or a resync.
        if (pendingResync)
            AddResyncRequest(observer, subject, 2);
        teleportAfterCollection = true;
        var received = new List<(uint Tick, ServerMessage.MessageOneofCase Case, uint Subject)>();
        for (uint tick = 1; tick <= FirstSweepTickAfter(1); tick++)
        {
            simulation.SimulateTick(peers, tick);
            foreach (OutgoingMessage message in DrainAllMessages())
                received.Add((tick, message.Message.MessageCase, message.Message.PlayerLeft?.SubjectId ?? 0));
        }

        Assert.That(received, Is.EqualTo(new[] { (1u, ServerMessage.MessageOneofCase.PlayerLeft, subject.Value) }));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
    }
}
