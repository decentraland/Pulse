using Decentraland.Pulse;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RealmRace_SubjectMovesAfterSpatialCollection_DeliversAcceptedQueryThenRetiresNormally(bool previouslyVisible)
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
                    // Publish the next realm only after the old snapshot has been accepted.
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
            ? Array.Empty<ServerMessage.MessageOneofCase>()
            : new[] { ServerMessage.MessageOneofCase.PlayerJoined }));
        Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Realm, Is.EqualTo("old"));
        Assert.That(simulation.observerViews[observer][subject].LastSentSnapshot.Seq, Is.EqualTo(2u));
        AssertAcceptedViewSweptOnce(1);

        PlaceInRealm(subject, "old", 4, teleport: true);
        simulation.SimulateTick(peers, FirstSweepTickAfter(1) + SWEEP_CHECK_INTERVAL + 1);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealmRace_StaleListenerGridMembership_IsExcludedByInterestThenRetiresNormally(bool previouslyVisible)
    {
        MakeSceneListener(observer, new Dictionary<string, int[]> { ["old"] = [0] });
        PlaceInRealm(subject, "old", 2);
        if (previouslyVisible)
        {
            simulation.SimulateTick(peers, 0);
            Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("old"));
        }

        // A retained grid candidate can name a snapshot from an unannounced realm.
        snapshotBoard.Publish(subject, TestSnapshots.Make(seq: 3, serverTick: 30, realm: "new", isTeleport: true));
        simulation.SimulateTick(peers, 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages, Is.Empty);
        Assert.That(simulation.observerViews[observer].ContainsKey(subject), Is.EqualTo(previouslyVisible));

        // The teleport completes: the subject leaves the announced realm's grid.
        realmGrids.Set(subject, "new", Vector3.Zero);
        for (uint tick = 2; tick <= FirstSweepTickAfter(0); tick++)
            simulation.SimulateTick(peers, tick);
        Assert.That(DrainAllMessages().Select(message => message.Message.MessageCase), Is.EqualTo(previouslyVisible
            ? new[] { ServerMessage.MessageOneofCase.PlayerLeft }
            : Array.Empty<ServerMessage.MessageOneofCase>()));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
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
        Assert.That(DrainAllMessages(), Is.Empty);
        Assert.That(simulation.observerViews[observer][subject].LastSeenTick, Is.Zero);
        AssertAcceptedViewSweptOnce(0);
    }

    [Test]
    public void RealmRace_PlayerWithoutRealm_RejectsRealmedSubject()
    {
        UseSpatialInterest();
        PlaceInRealm(subject, "other", 2);
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainAllMessages(), Is.Empty);
    }

    [Test]
    public void RealmRace_TierDelayedSubjectRoundTrip_KeepsIdentityAndDeliversTeleportOnTheNextDueTick()
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
            (4u, ServerMessage.MessageOneofCase.Teleported),
        }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RealmRace_TierDelayedSubjectTeleports_PreservesAcceptedQueryAndStaleGrace(bool pendingResync)
    {
        bool collectSubject = true;
        bool teleportAfterCollection = false;
        IAreaOfInterest aoi = Substitute.For<IAreaOfInterest>();
        aoi.When(x => x.GetVisibleSubjects(Arg.Any<PeerIndex>(), Arg.Any<PeerSnapshot>(), Arg.Any<IInterestCollector>()))
           .Do(call =>
            {
                if (collectSubject)
                {
                    IdentityRegistration? identity = identityBoard.GetIdentity(subject);
                    if (identity != null && snapshotBoard.TryRead(subject, out PeerSnapshot snapshot))
                        call.ArgAt<IInterestCollector>(2).Add(subject, PeerViewSimulationTier.TIER_2, snapshot.Seq, identity);
                }

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

        // Tick 1 keeps the accepted old view; a pending resync can deliver that captured state.
        if (pendingResync)
            AddResyncRequest(observer, subject, 2);
        teleportAfterCollection = true;
        var received = new List<(uint Tick, ServerMessage.MessageOneofCase Case)>();
        for (uint tick = 1; tick <= FirstSweepTickAfter(1); tick++)
        {
            simulation.SimulateTick(peers, tick);
            foreach (OutgoingMessage message in DrainAllMessages())
            {
                received.Add((tick, message.Message.MessageCase));
                if (message.Message.PlayerStateFull is { } state)
                {
                    Assert.That(state.Sequence, Is.EqualTo(2u));
                    Assert.That(state.SubjectId, Is.EqualTo(subject.Value));
                }
            }
        }

        Assert.That(received, Is.EqualTo(pendingResync
            ? new[] { (1u, ServerMessage.MessageOneofCase.PlayerStateFull), (FirstSweepTickAfter(1), ServerMessage.MessageOneofCase.PlayerLeft) }
            : new[] { (FirstSweepTickAfter(1), ServerMessage.MessageOneofCase.PlayerLeft) }));
        Assert.That(simulation.observerViews[observer], Does.Not.ContainKey(subject));
    }
}
