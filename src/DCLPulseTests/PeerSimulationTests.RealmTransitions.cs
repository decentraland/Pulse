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
    public void RealmTransition_SubjectRoundTrip_ReseedsRetainedView(int trailingSnapshots)
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
        Assert.That(DrainAllMessages().Select(m => m.Message.MessageCase), Is.EqualTo(new[]
        {
            ServerMessage.MessageOneofCase.PlayerLeft,
            ServerMessage.MessageOneofCase.PlayerJoined,
        }));
    }

    [Test]
    public void RealmTransition_ReusedObserverIndex_StartsWithFreshVisibility()
    {
        UseSpatialInterest();
        PlaceInRealm(observer, "old", 2);
        PlaceInRealm(subject, "old", 2);
        simulation.SimulateTick(peers, 0);
        DrainAllMessages();
        simulation.RemoveObserver(observer);
        snapshotBoard.ClearActive(observer);
        snapshotBoard.SetActive(observer);
        PlaceInRealm(observer, "new", 1);
        PlaceInRealm(subject, "new", 3);
        simulation.SimulateTick(peers, 1);
        Assert.That(DrainSingleMessage().Message.PlayerJoined.Realm, Is.EqualTo("new"));
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
