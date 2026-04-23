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
public class EmoteStartHandlerTests
{
    private const uint MONOTONIC_TIME = 5000;

    private SnapshotBoard snapshotBoard;
    private SpatialGrid spatialGrid;
    private ParcelEncoder parcelEncoder;
    private ITimeProvider timeProvider;
    private EmoteStartHandler handler;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        snapshotBoard = new SnapshotBoard(100, 16);
        spatialGrid = new SpatialGrid(100, 100);
        parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(MONOTONIC_TIME);

        handler = new EmoteStartHandler(snapshotBoard, spatialGrid, timeProvider,
            Substitute.For<ILogger<EmoteStartHandler>>(), parcelEncoder,
            new DiscreteEventRateLimiter(
                Options.Create(new DiscreteEventRateLimiterOptions { RatePerSecond = 0 }),
                timeProvider,
                Substitute.For<ITransport>()));
        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [Test]
    public void Handle_AuthenticatedPeer_SetsEmoteFieldsOnSnapshot()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateEmoteMessage("wave", durationMs: 3000));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote?.EmoteId, Is.EqualTo("wave"));
        Assert.That(snapshot.Emote?.StartTick, Is.EqualTo(MONOTONIC_TIME));
        Assert.That(snapshot.Emote?.DurationMs, Is.EqualTo(3000u));
    }

    [Test]
    public void Handle_AuthenticatedPeer_LoopingEmote_StoresNullDuration()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateEmoteMessage("dance"));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote?.DurationMs, Is.Null);
    }

    [Test]
    public void Handle_UnauthenticatedPeer_SkipsProcessing()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.PENDING_AUTH);

        handler.Handle(peers, peer, CreateEmoteMessage("wave", durationMs: 3000));

        Assert.That(snapshotBoard.TryRead(peer, out _), Is.False);
    }

    [Test]
    public void Handle_UnknownPeer_SkipsProcessing()
    {
        var peer = new PeerIndex(1);

        handler.Handle(peers, peer, CreateEmoteMessage("wave", durationMs: 3000));

        Assert.That(snapshotBoard.TryRead(peer, out _), Is.False);
    }

    [Test]
    public void Handle_PublishesSnapshotFromPlayerState()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        ClientMessage message = CreateEmoteMessage("wave", durationMs: 3000,
            position: new Vector3(3f, 1f, 7f), rotationY: 1.5f,
            movementBlend: 0.8f, slideBlend: 0.2f,
            stateFlags: (uint)PlayerAnimationFlags.Grounded);

        handler.Handle(peers, peer, message);

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.LocalPosition, Is.EqualTo(new Vector3(3f, 1f, 7f)));
        Assert.That(snapshot.RotationY, Is.EqualTo(1.5f));
        Assert.That(snapshot.MovementBlend, Is.EqualTo(0.8f));
        Assert.That(snapshot.SlideBlend, Is.EqualTo(0.2f));
        Assert.That(snapshot.AnimationFlags, Is.EqualTo(PlayerAnimationFlags.Grounded));
        Assert.That(snapshot.ServerTick, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void Handle_BumpsSnapshotSeq()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        snapshotBoard.Publish(peer, new PeerSnapshot(Seq: 5, ServerTick: 4000, Parcel: 0,
            default, default, default, 0f, 0, 0f, 0f, null, null, default, default));

        handler.Handle(peers, peer, CreateEmoteMessage("wave", durationMs: 3000));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot updated), Is.True);
        Assert.That(updated.Seq, Is.EqualTo(6));
    }

    [Test]
    public void Handle_UpdatesSpatialGrid()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateEmoteMessage("wave", durationMs: 3000,
            position: new Vector3(5f, 0f, 5f)));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(spatialGrid.GetPeers(snapshot.GlobalPosition), Does.Contain(peer));
    }

    [Test]
    public void Handle_DoesNotPublishSnapshot_WhenNoSnapshotExists()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);

        handler.Handle(peers, peer, CreateEmoteMessage("wave", durationMs: 3000));

        Assert.That(snapshotBoard.TryRead(peer, out _), Is.False);
    }

    private static ClientMessage CreateEmoteMessage(
        string emoteId,
        uint? durationMs = null,
        int parcelIndex = 0,
        Vector3? position = null,
        Vector3? velocity = null,
        float rotationY = 0f,
        float movementBlend = 0f,
        float slideBlend = 0f,
        uint stateFlags = 0)
    {
        var pos = position ?? Vector3.Zero;
        var vel = velocity ?? Vector3.Zero;

        var emoteStart = new EmoteStart
        {
            EmoteId = emoteId,
            PlayerState = new PlayerState
            {
                ParcelIndex = parcelIndex,
                Position = new Decentraland.Common.Vector3 { X = pos.X, Y = pos.Y, Z = pos.Z },
                Velocity = new Decentraland.Common.Vector3 { X = vel.X, Y = vel.Y, Z = vel.Z },
                RotationY = rotationY,
                MovementBlend = movementBlend,
                SlideBlend = slideBlend,
                StateFlags = stateFlags,
            },
        };

        if (durationMs.HasValue)
            emoteStart.DurationMs = durationMs.Value;

        return new ClientMessage { EmoteStart = emoteStart };
    }
}
