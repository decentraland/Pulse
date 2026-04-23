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

    private static PeerSnapshot MakeSnapshot(uint seq, string? realm, EmoteState? emote = null) =>
        new (Seq: seq, ServerTick: seq * 10,
            Parcel: 0,
            LocalPosition: Vector3.Zero, Velocity: Vector3.Zero,
            GlobalPosition: Vector3.Zero,
            RotationY: 0f, MovementBlend: 0f, JumpCount: 0, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed,
            Emote: emote,
            Realm: realm);
}
