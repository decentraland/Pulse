using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class SnapshotBoardRealmTests
{
    private const int MAX_PEERS = 16;
    private const int RING_CAPACITY = 4;

    private SnapshotBoard board;
    private PeerIndex peer;

    [SetUp]
    public void SetUp()
    {
        board = new SnapshotBoard(MAX_PEERS, RING_CAPACITY);
        peer = new PeerIndex(1);
        board.SetActive(peer);
    }

    [Test]
    public void Publish_FirstSnapshotWithoutRealm_StoresNullRealm()
    {
        board.Publish(peer, MakeSnapshot(seq: 1, realm: null));

        Assert.That(board.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.Null);
    }

    [Test]
    public void Publish_SnapshotWithExplicitRealm_StoresIt()
    {
        board.Publish(peer, MakeSnapshot(seq: 1, realm: "realm-a"));

        Assert.That(board.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
    }

    [Test]
    public void Publish_SnapshotWithoutRealm_AfterRealmIsSet_InheritsFromPrevious()
    {
        // Simulates the production flow: TeleportHandler publishes with an explicit realm, then
        // PlayerStateInputHandler publishes without one — the latest snapshot must still carry it.
        board.Publish(peer, MakeSnapshot(seq: 1, realm: "realm-a"));
        board.Publish(peer, MakeSnapshot(seq: 2, realm: null));

        Assert.That(board.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
        Assert.That(snapshot.Seq, Is.EqualTo(2));
    }

    [Test]
    public void Publish_SnapshotWithNewRealm_OverridesInherited()
    {
        // Realm change (TeleportRequest to a different realm) — the explicit value wins, carry-
        // forward only applies when the incoming snapshot has Realm == null.
        board.Publish(peer, MakeSnapshot(seq: 1, realm: "realm-a"));
        board.Publish(peer, MakeSnapshot(seq: 2, realm: null)); // inherits realm-a
        board.Publish(peer, MakeSnapshot(seq: 3, realm: "realm-b"));
        board.Publish(peer, MakeSnapshot(seq: 4, realm: null)); // inherits realm-b

        Assert.That(board.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-b"));
        Assert.That(snapshot.Seq, Is.EqualTo(4));
    }

    [Test]
    public void Publish_RealmCarriesForwardAcrossRingWrap()
    {
        // Ring capacity is 4; the explicit-realm snapshot at seq 1 will be evicted once seq 5 is
        // written. Carry-forward must keep the realm accurate on the latest slot regardless.
        board.Publish(peer, MakeSnapshot(seq: 1, realm: "realm-a"));

        for (uint s = 2; s <= 10; s++)
            board.Publish(peer, MakeSnapshot(seq: s, realm: null));

        Assert.That(board.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
        Assert.That(snapshot.Seq, Is.EqualTo(10));
    }

    [Test]
    public void Publish_RealmAndEmoteInheritedIndependently()
    {
        // Both ledgers carry forward, neither interferes with the other. Emote stop consumes the
        // emote state; realm is unaffected.
        board.Publish(peer, MakeSnapshot(seq: 1, realm: "realm-a"));

        board.Publish(peer, MakeSnapshot(seq: 2, realm: null,
            emote: new EmoteState("wave", StartSeq: 2, StartTick: 100)));

        board.Publish(peer, MakeSnapshot(seq: 3, realm: null)); // inherits both

        Assert.That(board.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
        Assert.That(snapshot.Emote?.EmoteId, Is.EqualTo("wave"));
    }

    [Test]
    public void ClearActive_ResetsRealmForNextPublisher()
    {
        board.Publish(peer, MakeSnapshot(seq: 1, realm: "realm-a"));
        board.ClearActive(peer);

        // New session on the same slot (recycled PeerIndex). A publish with no realm must not
        // inherit from the pre-disconnect snapshot — the ring was cleared.
        board.SetActive(peer);
        board.Publish(peer, MakeSnapshot(seq: 1, realm: null));

        Assert.That(board.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.Null);
    }

    [Test]
    public void Publish_RealmGeneration_ChangesOnlyWithRealmTransitions()
    {
        Assert.That(board.Publish(peer, MakeSnapshot(1, null)).RealmGeneration, Is.Zero);
        Assert.That(board.Publish(peer, MakeSnapshot(2, "a")).RealmGeneration, Is.EqualTo(1ul));
        Assert.That(board.Publish(peer, MakeSnapshot(3, "a")).RealmGeneration, Is.EqualTo(1ul));
        Assert.That(board.Publish(peer, MakeSnapshot(4, null)).RealmGeneration, Is.EqualTo(1ul));
        Assert.That(board.Publish(peer, MakeSnapshot(5, "b")).RealmGeneration, Is.EqualTo(2ul));
        Assert.That(board.Publish(peer, MakeSnapshot(6, "a")).RealmGeneration, Is.EqualTo(3ul));
    }

    [Test]
    public void Publish_RealmGeneration_SurvivesRoundTripAndCompleteHistoryEviction()
    {
        board.Publish(peer, MakeSnapshot(1, "a"));
        board.Publish(peer, MakeSnapshot(2, "b"));
        board.Publish(peer, MakeSnapshot(3, "a"));
        for (uint seq = 4; seq <= RING_CAPACITY * 3; seq++)
            board.Publish(peer, MakeSnapshot(seq, null));

        Assert.That(board.TryRead(peer, 2, out _), Is.False);
        Assert.That(board.TryRead(peer, 3, out _), Is.False);
        Assert.That(board.TryRead(peer, out PeerSnapshot latest), Is.True);
        Assert.That(latest.RealmGeneration, Is.EqualTo(3ul));
        Assert.That(latest.Realm, Is.EqualTo("a"));
    }

    [Test]
    public void Publish_RealmGeneration_IsDerivedRatherThanCopiedFromCaller()
    {
        board.Publish(peer, MakeSnapshot(1, "a"));
        PeerSnapshot stored = board.Publish(peer, MakeSnapshot(2, null) with { RealmGeneration = 100 });
        Assert.That(stored.RealmGeneration, Is.EqualTo(1ul));
    }

    [Test]
    public void ClearActive_ResetsRealmGenerationForReusedPeerIndex()
    {
        board.Publish(peer, MakeSnapshot(1, "a"));
        board.Publish(peer, MakeSnapshot(2, "b"));
        board.ClearActive(peer);
        board.SetActive(peer);
        Assert.That(board.Publish(peer, MakeSnapshot(1, null)).RealmGeneration, Is.Zero);
        Assert.That(board.Publish(peer, MakeSnapshot(2, "b")).RealmGeneration, Is.EqualTo(1ul));
    }

    private static PeerSnapshot MakeSnapshot(uint seq, string? realm, EmoteState? emote = null) =>
        TestSnapshots.Make(seq: seq, serverTick: seq * 10,
            emote: emote,
            realm: realm);
}
