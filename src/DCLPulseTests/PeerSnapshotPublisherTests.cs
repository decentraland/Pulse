using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class PeerSnapshotPublisherTests
{
    private const uint NOW = 5_000;

    private SnapshotBoard snapshotBoard;
    private SpatialGrid spatialGrid;
    private ParcelEncoder parcelEncoder;
    private ITimeProvider timeProvider;
    private PeerSnapshotPublisher publisher;
    private PeerIndex peer;

    [SetUp]
    public void SetUp()
    {
        snapshotBoard = new SnapshotBoard(100, 16);
        spatialGrid = new SpatialGrid(100, 100);
        parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(NOW);

        publisher = new PeerSnapshotPublisher(snapshotBoard, spatialGrid, parcelEncoder, timeProvider);

        peer = new PeerIndex(1);
        snapshotBoard.SetActive(peer);
    }

    [Test]
    public void PublishFromPlayerState_FreshSlot_PublishesAtSeqZero()
    {
        publisher.PublishFromPlayerState(peer, MakeState(parcelIndex: 5, position: new Vector3(1f, 2f, 3f)));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(0u));
        Assert.That(snapshot.ServerTick, Is.EqualTo(NOW));
        Assert.That(snapshot.Parcel, Is.EqualTo(5));
        Assert.That(snapshot.LocalPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
    }

    [Test]
    public void PublishFromPlayerState_BumpsSeqOnSubsequentPublishes()
    {
        publisher.PublishFromPlayerState(peer, MakeState());
        publisher.PublishFromPlayerState(peer, MakeState());
        publisher.PublishFromPlayerState(peer, MakeState());

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(2u));
    }

    [Test]
    public void PublishFromPlayerState_StampsEmoteStartSeqWithSnapshotSeq()
    {
        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: 41, ServerTick: 0, Parcel: 0,
            LocalPosition: default(Vector3), GlobalPosition: default(Vector3), Velocity: default(Vector3), RotationY: 0,
            JumpCount: 0, MovementBlend: 0, SlideBlend: 0,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: default(PlayerAnimationFlags), GlideState: default(GlideState)));

        // EmoteInput exposes only the fields the caller owns — StartSeq is bookkeeping that the
        // publisher fills in to match the new snapshot's Seq.
        var emote = new PeerSnapshotPublisher.EmoteInput("wave", DurationMs: 1000);
        publisher.PublishFromPlayerState(peer, MakeState(), emote);

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(42u));

        Assert.That(snapshot.Emote!.Value.StartSeq, Is.EqualTo(42u),
            "Publisher must stamp EmoteState.StartSeq to match the new snapshot's Seq.");

        Assert.That(snapshot.Emote.Value.EmoteId, Is.EqualTo("wave"));
        Assert.That(snapshot.Emote.Value.DurationMs, Is.EqualTo(1000u));
    }

    [Test]
    public void PublishFromPlayerState_NullStartTick_DefaultsToServerTick()
    {
        var emote = new PeerSnapshotPublisher.EmoteInput("wave", DurationMs: 1000);
        publisher.PublishFromPlayerState(peer, MakeState(), emote);

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);

        Assert.That(snapshot.Emote!.Value.StartTick, Is.EqualTo(NOW),
            "Null StartTick must default to the snapshot's ServerTick — the EmoteStart 'started right now' case.");
    }

    [Test]
    public void PublishFromPlayerState_ExplicitStartTick_PreservedVerbatim()
    {
        // Backdated start: the handshake reconnect path passes a StartTick older than ServerTick
        // so observers scrub the animation forward.
        var emote = new PeerSnapshotPublisher.EmoteInput("wave", DurationMs: 1000, StartTick: NOW - 1_500);
        publisher.PublishFromPlayerState(peer, MakeState(), emote);

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote!.Value.StartTick, Is.EqualTo(NOW - 1_500));
        Assert.That(snapshot.ServerTick, Is.EqualTo(NOW));
    }

    [Test]
    public void PublishFromPlayerState_NullEmote_LeavesSnapshotEmoteNull()
    {
        publisher.PublishFromPlayerState(peer, MakeState());

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote, Is.Null);
    }

    [Test]
    public void PublishFromPlayerState_RefreshesSpatialGridAtNewGlobalPosition()
    {
        int parcelIndex = parcelEncoder.Encode(7, 11);
        publisher.PublishFromPlayerState(peer, MakeState(parcelIndex: parcelIndex, position: new Vector3(2f, 0f, 4f)));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(spatialGrid.GetPeers(snapshot.GlobalPosition), Does.Contain(peer));
    }

    [Test]
    public void PublishTeleport_WithoutPriorSnapshot_DefaultsRotationAndHeadIK()
    {
        publisher.PublishTeleport(peer, parcelIndex: 0, localPosition: new Vector3(1f, 0f, 1f), realm: "realm-a");

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.IsTeleport, Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
        Assert.That(snapshot.RotationY, Is.EqualTo(0f));
        Assert.That(snapshot.HeadYaw, Is.Null);
        Assert.That(snapshot.HeadPitch, Is.Null);
        Assert.That(snapshot.Velocity, Is.EqualTo(Vector3.Zero));
        Assert.That(snapshot.AnimationFlags, Is.EqualTo(PlayerAnimationFlags.Grounded));
        Assert.That(snapshot.GlideState, Is.EqualTo(GlideState.PropClosed));
    }

    [Test]
    public void PublishTeleport_WithPriorSnapshot_InheritsRotationAndHeadIK()
    {
        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: 0, ServerTick: NOW, Parcel: 0,
            LocalPosition: default(Vector3), GlobalPosition: default(Vector3), Velocity: default(Vector3),
            RotationY: 1.5f,
            JumpCount: 0, MovementBlend: 0, SlideBlend: 0,
            HeadYaw: 0.4f, HeadPitch: -0.2f,
            AnimationFlags: default(PlayerAnimationFlags), GlideState: default(GlideState),
            Realm: "realm-prior"));

        publisher.PublishTeleport(peer, parcelIndex: 0, localPosition: new Vector3(1f, 0f, 1f), realm: "realm-b");

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.RotationY, Is.EqualTo(1.5f));
        Assert.That(snapshot.HeadYaw, Is.EqualTo(0.4f));
        Assert.That(snapshot.HeadPitch, Is.EqualTo(-0.2f));

        Assert.That(snapshot.Realm, Is.EqualTo("realm-b"),
            "Realm is set from the teleport request — never inherited.");
    }

    [Test]
    public void PublishTeleport_RefreshesSpatialGridAtNewGlobalPosition()
    {
        int parcelIndex = parcelEncoder.Encode(9, 14);
        publisher.PublishTeleport(peer, parcelIndex, localPosition: new Vector3(3f, 0f, 5f), realm: "realm-a");

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(spatialGrid.GetPeers(snapshot.GlobalPosition), Does.Contain(peer));
    }

    private static PlayerState MakeState(int parcelIndex = 0, Vector3? position = null, Vector3? velocity = null)
    {
        Vector3 pos = position ?? Vector3.Zero;
        Vector3 vel = velocity ?? Vector3.Zero;

        return new PlayerState
        {
            ParcelIndex = parcelIndex,
            Position = new Decentraland.Common.Vector3 { X = pos.X, Y = pos.Y, Z = pos.Z },
            Velocity = new Decentraland.Common.Vector3 { X = vel.X, Y = vel.Y, Z = vel.Z },
        };
    }
}
