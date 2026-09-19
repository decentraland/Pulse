using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private void UseSpatialInterest()
    {
        areaOfInterest = new SpatialHashAreaOfInterest(realmGrids, snapshotBoard,
            Options.Create(new SpatialHashAreaOfInterestOptions()));
        simulation = new PeerSimulation(areaOfInterest, snapshotBoard, realmGrids, identityBoard,
            messagePipe, SimulationSteps, timeProvider, Substitute.For<ITransport>(),
            profileBoard, peerIndexAllocator, Substitute.For<ILogger<PeerSimulation>>());
    }

    private void PlaceInRealm(PeerIndex peer, string realm, uint seq, bool teleport = false, EmoteState? emote = null)
    {
        snapshotBoard.Publish(peer, TestSnapshots.Make(seq: seq, serverTick: seq * 10,
            realm: realm, isTeleport: teleport, emote: emote));
        realmGrids.Set(peer, realm, Vector3.Zero);
    }

    [TestCase(true, 0)]
    [TestCase(true, 20)]
    [TestCase(true, 59)]
    [TestCase(true, 81)]
    [TestCase(false, 0)]
    [TestCase(false, 20)]
    [TestCase(false, 59)]
    [TestCase(false, 81)]
    public void RealmTransition_Coteleport_ReseedsIdentityBeforeFurtherMovement(bool observerFirst, int gapTicks)
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));

        PlaceInRealm(observerFirst ? observer : subject, "new", 3, teleport: true);
        for (uint tick = 1; tick <= gapTicks; tick++)
        {
            simulation.SimulateTick(peers, tick);
            Assert.That(DrainAllMessages().All(m => m.Message.PlayerLeft != null), Is.True,
                "Peers in different realms must not receive each other's state or identities");
        }

        PlaceInRealm(observerFirst ? subject : observer, "new", 3, teleport: true);
        simulation.SimulateTick(peers, (uint)gapTicks + 1);
        List<OutgoingMessage> messages = DrainAllMessages();
        OutgoingMessage joined = messages.Single(m => m.Message.PlayerJoined != null);
        Assert.Multiple(() =>
        {
            Assert.That(joined.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
            Assert.That(joined.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
            Assert.That(joined.Message.PlayerJoined.Realm, Is.EqualTo("new"));
            Assert.That(joined.Message.PlayerJoined.State.Sequence, Is.EqualTo(3u));
            Assert.That(messages.Last().Message.PlayerJoined, Is.Not.Null,
                "No delayed PlayerLeft may undo the newly restored identity");
            Assert.That(messages.Any(m => m.Message.Teleported != null), Is.False);
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
    public void RealmTransition_ReseedsCurrentProfileAndActiveEmote_AndClearsOldResync()
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
        Assert.That(peers[observer].ResyncRequests, Is.Empty);
        simulation.SimulateTick(peers, 2);
        Assert.That(DrainAllMessages(), Is.Empty, "Reseeding must not replay the same emote or profile every tick");
    }

    [Test]
    public void RealmTransition_RetainedSubjectChangesRealm_ReseedsForMultiRealmListener()
    {
        UseSpatialInterest();
        MakeSceneListener(observer, new Dictionary<string, int[]> { ["old"] = [0], ["new"] = [0] });
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
}
