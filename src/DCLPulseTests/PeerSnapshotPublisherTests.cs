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

        Vector3 decoded = snapshot.DecodePosition();
        Assert.That(decoded.X, Is.EqualTo(1f).Within(PlayerState.PositionXQuantizedStep));
        Assert.That(decoded.Y, Is.EqualTo(2f).Within(PlayerState.PositionYQuantizedStep));
        Assert.That(decoded.Z, Is.EqualTo(3f).Within(PlayerState.PositionZQuantizedStep));
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
        snapshotBoard.Publish(peer, TestSnapshots.Make(seq: 41));

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
    public void PublishFromPlayerState_WithoutRealm_InheritsRealmFromPriorSnapshot()
    {
        // Mirrors production: HandshakeHandler seeds with realm, then PlayerStateInputHandler
        // publishes movement without realm — the latest ring slot must still carry the realm
        // forward so AoI lookups don't see a null realm one tick after the seed.
        publisher.PublishFromPlayerState(peer, MakeState(), emote: null, realm: "main");
        publisher.PublishFromPlayerState(peer, MakeState());

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(1u));

        Assert.That(snapshot.Realm, Is.EqualTo("main"),
            "Callers that omit realm must inherit it from the prior snapshot, not overwrite with null.");
    }

    [Test]
    public void PublishTeleport_WithoutPriorSnapshot_DefaultsRotationAndHeadIKAndPointAt()
    {
        publisher.PublishTeleport(peer, parcelIndex: 0, localPosition: new Vector3(1f, 0f, 1f), realm: "realm-a");

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.IsTeleport, Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
        Assert.That(snapshot.DecodeRotationY(), Is.EqualTo(0f));
        Assert.That(snapshot.HeadYaw, Is.Null);
        Assert.That(snapshot.HeadPitch, Is.Null);
        Assert.That(snapshot.PointAt, Is.Null);
        Assert.That(snapshot.DecodeVelocity(), Is.EqualTo(Vector3.Zero));
        Assert.That(snapshot.AnimationFlags, Is.EqualTo(PlayerAnimationFlags.Grounded));
        Assert.That(snapshot.GlideState, Is.EqualTo(GlideState.PropClosed));
    }

    [Test]
    public void PublishTeleport_WithPriorSnapshot_InheritsRotationAndHeadIKAndPointAt()
    {
        PeerSnapshot prior = TestSnapshots.Make(
            seq: 0, serverTick: NOW,
            rotationY: 90f, headYaw: 45f, headPitch: 30f,
            pointAt: new Vector3(7f, 8f, 9f), realm: "realm-prior");
        snapshotBoard.Publish(peer, prior);

        publisher.PublishTeleport(peer, parcelIndex: 0, localPosition: new Vector3(1f, 0f, 1f), realm: "realm-b");

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        // Rotation / head-IK / point-at are inherited from the prior snapshot verbatim as raw codes.
        Assert.That(snapshot.RotationY, Is.EqualTo(prior.RotationY));
        Assert.That(snapshot.HeadYaw, Is.EqualTo(prior.HeadYaw));
        Assert.That(snapshot.HeadPitch, Is.EqualTo(prior.HeadPitch));
        Assert.That(snapshot.PointAt, Is.EqualTo(prior.PointAt));

        Assert.That(snapshot.Realm, Is.EqualTo("realm-b"),
            "Realm is set from the teleport request — never inherited.");
    }

    [Test]
    public void PublishTeleport_GlobalPositionAgreesWithBroadcastCodes_WhenPositionExceedsQuantizationRange()
    {
        // The quantized setters clamp the stored codes to [0,16]x[0,200]x[0,16]; GlobalPosition
        // (AoI placement) must be derived from those same codes, or the server indexes the peer
        // where no observer renders it. ValidateTeleport only checks finiteness, so an
        // out-of-range position reaches the publisher.
        publisher.PublishTeleport(peer, parcelIndex: 0, localPosition: new Vector3(20f, 250f, -3f), realm: "realm-a");

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Vector3 expected = parcelEncoder.DecodeToGlobalPosition(0, snapshot.DecodePosition());
        Assert.That(snapshot.GlobalPosition, Is.EqualTo(expected),
            "GlobalPosition must match what observers decode from the broadcast position codes.");
    }

    [Test]
    public void PublishTeleport_RefreshesSpatialGridAtNewGlobalPosition()
    {
        int parcelIndex = parcelEncoder.Encode(9, 14);
        publisher.PublishTeleport(peer, parcelIndex, localPosition: new Vector3(3f, 0f, 5f), realm: "realm-a");

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(spatialGrid.GetPeers(snapshot.GlobalPosition), Does.Contain(peer));
    }

    [Test]
    public void PublishFromPlayerState_PointAtWithoutPointingAtFlag_IsIgnored()
    {
        // point_at is "only meaningful when POINTING_AT is set in state_flags" per the proto.
        // A client that sends a vector without the flag must be treated as not pointing.
        PlayerState state = MakeState();
        state.PointAtXQuantized = 5f;
        state.PointAtYQuantized = 6f;
        state.PointAtZQuantized = 7f;

        publisher.PublishFromPlayerState(peer, state);

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.PointAt, Is.Null);
    }

    [Test]
    public void PublishFromPlayerState_PointAtWithPointingAtFlag_IsLifted()
    {
        PlayerState state = MakeState();
        state.PointAtXQuantized = 5f;
        state.PointAtYQuantized = 6f;
        state.PointAtZQuantized = 7f;
        state.StateFlags = (uint)PlayerAnimationFlags.PointingAt;

        publisher.PublishFromPlayerState(peer, state);

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Vector3? pointAt = snapshot.DecodePointAt();
        Assert.That(pointAt.HasValue, Is.True);
        Assert.That(pointAt!.Value.X, Is.EqualTo(5f).Within(PlayerState.PointAtXQuantizedStep));
        Assert.That(pointAt.Value.Y, Is.EqualTo(6f).Within(PlayerState.PointAtYQuantizedStep));
        Assert.That(pointAt.Value.Z, Is.EqualTo(7f).Within(PlayerState.PointAtZQuantizedStep));
    }

    private static PlayerState MakeState(int parcelIndex = 0, Vector3? position = null, Vector3? velocity = null)
    {
        Vector3 pos = position ?? Vector3.Zero;
        Vector3 vel = velocity ?? Vector3.Zero;

        return new PlayerState
        {
            ParcelIndex = parcelIndex,
            PositionXQuantized = pos.X,
            PositionYQuantized = pos.Y,
            PositionZQuantized = pos.Z,
            VelocityXQuantized = vel.X,
            VelocityYQuantized = vel.Y,
            VelocityZQuantized = vel.Z,
        };
    }
}
