using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class PlayerStateInputHandlerTests
{
    private const uint MONOTONIC_TIME = 5000;

    private ITimeProvider timeProvider;
    private SnapshotBoard snapshotBoard;
    private SpatialGrid spatialGrid;
    private ParcelEncoder parcelEncoder;
    private PlayerStateInputHandler handler;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(MONOTONIC_TIME);

        snapshotBoard = new SnapshotBoard(100, 10);
        spatialGrid = new SpatialGrid(100, 100);

        var options = Options.Create(new ParcelEncoderOptions());
        parcelEncoder = new ParcelEncoder(options);

        var publisher = new PeerSnapshotPublisher(snapshotBoard, spatialGrid, parcelEncoder, timeProvider);

        handler = new PlayerStateInputHandler(
            snapshotBoard,
            publisher,
            Substitute.For<ILogger<PlayerStateInputHandler>>(),
            new MovementInputRateLimiter(
                Options.Create(new MovementInputRateLimiterOptions { MaxHz = 0 }),
                timeProvider,
                Substitute.For<ITransport>()),
            new FieldValidator(
                Options.Create(new FieldValidatorOptions
                {
                    MaxRealmLength = 0, MaxEmoteDurationMs = 0,
                }),
                Options.Create(new SceneListenerOptions()),
                parcelEncoder,
                Substitute.For<ITransport>()));

        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [Test]
    public void Handle_UnauthenticatedPeer_SkipsProcessing()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.PENDING_AUTH);

        ClientMessage message = CreateInputMessage();

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.LastSeq(peerIndex), Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void Handle_UnknownPeer_SkipsProcessing()
    {
        var peerIndex = new PeerIndex(1);
        ClientMessage message = CreateInputMessage();

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.LastSeq(peerIndex), Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void Handle_PendingDisconnectPeer_SkipsProcessing()
    {
        // Queued messages arriving after a defense already condemned the peer must be dropped
        // silently — no snapshot publish, no re-triggering of defenses.
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.PENDING_DISCONNECT);

        handler.Handle(peers, peerIndex, CreateInputMessage());

        Assert.That(snapshotBoard.LastSeq(peerIndex), Is.EqualTo(uint.MaxValue),
            "PENDING_DISCONNECT peer's input must not advance the snapshot sequence");
    }

    [Test]
    public void Handle_AuthenticatedPeer_PublishesSnapshot()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage message = CreateInputMessage(parcelIndex: 1, position: new Vector3(3, 5, 7));

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(0));
        Assert.That(snapshot.ServerTick, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void Handle_AuthenticatedPeer_SnapshotContainsInputState()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage message = CreateInputMessage(
            rotationY: 1.5f,
            movementBlend: 0.7f,
            slideBlend: 0.3f,
            stateFlags: (uint)PlayerAnimationFlags.Grounded);

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.DecodeRotationY(), Is.EqualTo(1.5f).Within(PlayerState.RotationYQuantizedStep));
        Assert.That(snapshot.DecodeMovementBlend(), Is.EqualTo(0.7f).Within(PlayerState.MovementBlendQuantizedStep));
        Assert.That(snapshot.DecodeSlideBlend(), Is.EqualTo(0.3f).Within(PlayerState.SlideBlendQuantizedStep));
        Assert.That(snapshot.AnimationFlags, Is.EqualTo(PlayerAnimationFlags.Grounded));
    }

    [Test]
    public void Handle_AuthenticatedPeer_SequenceIncrementsOnEachCall()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        handler.Handle(peers, peerIndex, CreateInputMessage(parcelIndex: 3, new Vector3(3, 6, 2)));
        handler.Handle(peers, peerIndex, CreateInputMessage(velocity: new Vector3(1, 0, 7)));
        handler.Handle(peers, peerIndex, CreateInputMessage(stateFlags: (uint) PlayerAnimationFlags.Grounded));

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(2));
    }

    [Test]
    public void Handle_AuthenticatedPeer_UpdatesSpatialGrid()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage message = CreateInputMessage(movementBlend: 2);

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        HashSet<PeerIndex>? gridPeers = spatialGrid.GetPeers(snapshot.GlobalPosition);
        Assert.That(gridPeers, Is.Not.Null);
        Assert.That(gridPeers, Does.Contain(peerIndex));
    }

    [TestCase(5, 10, 2f, 3f, 4f, 82, 3f, 164)]
    [TestCase(162, -150, 1f, 5f, 2f, 2593f, 5f, -2398f)]
    [TestCase(161, -152, 0f, 0f, 0f, 2576f, 0f, -2432f)]
    public void Handle_AuthenticatedPeer_ComputesGlobalPositionFromParcel(
        int parcelX, int parcelZ,
        float localX, float localY, float localZ,
        float expectedGlobalX, float expectedGlobalY, float expectedGlobalZ)
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        var localPosition = new Vector3(localX, localY, localZ);
        int parcelIndex = parcelEncoder.Encode(parcelX, parcelZ);
        ClientMessage message = CreateInputMessage(parcelIndex: parcelIndex, position: localPosition);

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        // GlobalPosition is derived from the decoded position codes, so it carries the same
        // quantization error — assert per axis within the field's step, not exact equality.
        Assert.That(snapshot.GlobalPosition.X, Is.EqualTo(expectedGlobalX).Within(PlayerState.PositionXQuantizedStep));
        Assert.That(snapshot.GlobalPosition.Y, Is.EqualTo(expectedGlobalY).Within(PlayerState.PositionYQuantizedStep));
        Assert.That(snapshot.GlobalPosition.Z, Is.EqualTo(expectedGlobalZ).Within(PlayerState.PositionZQuantizedStep));
        Vector3 localDecoded = snapshot.DecodePosition();
        Assert.That(localDecoded.X, Is.EqualTo(localPosition.X).Within(PlayerState.PositionXQuantizedStep));
        Assert.That(localDecoded.Y, Is.EqualTo(localPosition.Y).Within(PlayerState.PositionYQuantizedStep));
        Assert.That(localDecoded.Z, Is.EqualTo(localPosition.Z).Within(PlayerState.PositionZQuantizedStep));
    }

    [Test]
    public void Handle_DisconnectingPeer_SkipsProcessing()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.DISCONNECTING);

        handler.Handle(peers, peerIndex, CreateInputMessage());

        Assert.That(snapshotBoard.LastSeq(peerIndex), Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void Handle_WithHeadTracking_SnapshotContainsHeadValues()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage message = CreateInputMessage(headYaw: 45f, headPitch: -15f);

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.DecodeHeadYaw().Value, Is.EqualTo(45f).Within(PlayerState.HeadYawQuantizedStep));
        // HeadPitch range is [0, 360]; -15f clamps to 0 on encode.
        Assert.That(snapshot.DecodeHeadPitch().Value, Is.EqualTo(0f).Within(PlayerState.HeadPitchQuantizedStep));
    }

    [Test]
    public void Handle_WithoutHeadTracking_SnapshotHasNullHeadValues()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage message = CreateInputMessage();

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.DecodeHeadYaw(), Is.Null);
        Assert.That(snapshot.DecodeHeadPitch(), Is.Null);
    }

    [Test]
    public void Handle_IdenticalState_DoesNotIncrementSeq()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage message = CreateInputMessage(
            position: new Vector3(1f, 2f, 3f),
            rotationY: 1.5f,
            stateFlags: (uint)PlayerAnimationFlags.Grounded);

        handler.Handle(peers, peerIndex, message);
        handler.Handle(peers, peerIndex, message);
        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(0));
    }

    [Test]
    public void Handle_IdenticalState_DoesNotUpdateSpatialGrid()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage message = CreateInputMessage(position: new Vector3(5f, 0f, 5f));

        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Vector3 firstGlobal = snapshot.GlobalPosition;

        // Send same state again — spatial grid should not be touched
        handler.Handle(peers, peerIndex, message);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot secondSnapshot), Is.True);
        Assert.That(secondSnapshot.Seq, Is.EqualTo(0));
        Assert.That(spatialGrid.GetPeers(firstGlobal), Does.Contain(peerIndex));
    }

    [Test]
    public void Handle_StateChangesAfterIdle_IncrementsSeq()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        ClientMessage idle = CreateInputMessage(position: new Vector3(1f, 0f, 0f));
        handler.Handle(peers, peerIndex, idle);
        handler.Handle(peers, peerIndex, idle);
        handler.Handle(peers, peerIndex, idle);

        // State changes — seq should go from 0 to 1, not 0 to 3
        ClientMessage moved = CreateInputMessage(position: new Vector3(5f, 0f, 0f));
        handler.Handle(peers, peerIndex, moved);

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(1));
    }

    [Test]
    public void Handle_OnlyAnimationFlagsChange_IncrementsSeq()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        handler.Handle(peers, peerIndex, CreateInputMessage(stateFlags: (uint)PlayerAnimationFlags.Falling));
        handler.Handle(peers, peerIndex, CreateInputMessage(stateFlags: (uint)PlayerAnimationFlags.Grounded));

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(1));
        Assert.That(snapshot.AnimationFlags, Is.EqualTo(PlayerAnimationFlags.Grounded));
    }

    [Test]
    public void Handle_OnlyHeadTrackingChanges_IncrementsSeq()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        handler.Handle(peers, peerIndex, CreateInputMessage(headYaw: 10f, headPitch: 5f));
        handler.Handle(peers, peerIndex, CreateInputMessage(headYaw: 20f, headPitch: 5f));

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(1));
        Assert.That(snapshot.DecodeHeadYaw().Value, Is.EqualTo(20f).Within(PlayerState.HeadYawQuantizedStep));
    }

    [Test]
    public void Handle_OnlyJumpCountChanges_IncrementsSeq()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        // A jump can land the next input on identical position/velocity/flags with only JumpCount
        // bumped. JumpCount is the sole per-jump animation signal (no "Jumping" flag), so the
        // republish must not be suppressed — otherwise observers never see the jump.
        // The position is held non-zero so the first input publishes (an all-zero input would match
        // the default snapshot TryRead returns before the first publish and be suppressed).
        var pos = new Vector3(1f, 0f, 0f);
        handler.Handle(peers, peerIndex, CreateInputMessage(position: pos, jumpCount: 0));
        handler.Handle(peers, peerIndex, CreateInputMessage(position: pos, jumpCount: 1));

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(1));
        Assert.That(snapshot.JumpCount, Is.EqualTo(1));
    }

    [Test]
    public void Handle_FirstMessage_AlwaysPublishes()
    {
        var peerIndex = new PeerIndex(1);
        peers[peerIndex] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peerIndex);

        // Even with all-zero state, the first message should publish
        handler.Handle(peers, peerIndex, CreateInputMessage());

        Assert.That(snapshotBoard.TryRead(peerIndex, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(0));
    }

    private static ClientMessage CreateInputMessage(
        int parcelIndex = 0,
        Vector3? position = null,
        Vector3? velocity = null,
        float rotationY = 0f,
        float movementBlend = 0f,
        float slideBlend = 0f,
        float? headYaw = null,
        float? headPitch = null,
        uint stateFlags = 0,
        int jumpCount = 0)
    {
        var pos = position ?? Vector3.Zero;
        var vel = velocity ?? Vector3.Zero;

        var state = new PlayerState
        {
            ParcelIndex = parcelIndex,
            PositionXQuantized = pos.X,
            PositionYQuantized = pos.Y,
            PositionZQuantized = pos.Z,
            VelocityXQuantized = vel.X,
            VelocityYQuantized = vel.Y,
            VelocityZQuantized = vel.Z,
            RotationYQuantized = rotationY,
            MovementBlendQuantized = movementBlend,
            SlideBlendQuantized = slideBlend,
            StateFlags = stateFlags,
            JumpCount = jumpCount
        };

        if (headYaw.HasValue)
            state.HeadYawQuantized = headYaw.Value;

        if (headPitch.HasValue)
            state.HeadPitchQuantized = headPitch.Value;

        return new ClientMessage
        {
            Input = new PlayerStateInput { State = state }
        };
    }
}
