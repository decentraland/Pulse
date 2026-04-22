using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class EmoteStopHandlerTests
{
    private const uint START_TIME = 5000;
    private const uint STOP_TIME = 6000;

    private SnapshotBoard snapshotBoard;
    private ITimeProvider timeProvider;
    private EmoteStopHandler handler;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        snapshotBoard = new SnapshotBoard(100, 16);
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(STOP_TIME);

        handler = new EmoteStopHandler(snapshotBoard, timeProvider, Substitute.For<ILogger<EmoteStopHandler>>());
        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [Test]
    public void Handle_ActiveEmote_PublishesStopSnapshot()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        // Publish an emote snapshot first
        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: 1, ServerTick: START_TIME, Parcel: 0,
            default(Vector3), default(Vector3), default(Vector3), 0f, 0, 0f, 0f, null, null, default(PlayerAnimationFlags), default(GlideState),
            Emote: new EmoteState("dance", StartSeq: 1, StartTick: START_TIME)));

        handler.Handle(peers, peer, new ClientMessage { EmoteStop = new EmoteStop() });

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote?.EmoteId, Is.Null);
        Assert.That(snapshot.Emote?.StopReason, Is.EqualTo(EmoteStopReason.Cancelled));
        Assert.That(snapshot.ServerTick, Is.EqualTo(STOP_TIME));
        Assert.That(snapshot.Seq, Is.EqualTo(2u));
    }

    [Test]
    public void Handle_NoActiveEmote_DoesNothing()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        // Publish a normal snapshot (no emote)
        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: 1, ServerTick: START_TIME, Parcel: 0,
            default(Vector3), default(Vector3), default(Vector3), 0f, 0, 0f, 0f, null, null, default(PlayerAnimationFlags), default(GlideState)));

        handler.Handle(peers, peer, new ClientMessage { EmoteStop = new EmoteStop() });

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(1u)); // no new snapshot published
    }

    [Test]
    public void Handle_UnauthenticatedPeer_SkipsProcessing()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.PENDING_AUTH);
        snapshotBoard.SetActive(peer);

        // Publish an emote snapshot
        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: 1, ServerTick: START_TIME, Parcel: 0,
            default(Vector3), default(Vector3), default(Vector3), 0f, 0, 0f, 0f, null, null, default(PlayerAnimationFlags), default(GlideState),
            Emote: new EmoteState("dance", StartSeq: 1, StartTick: START_TIME)));

        handler.Handle(peers, peer, new ClientMessage { EmoteStop = new EmoteStop() });

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote?.EmoteId, Is.EqualTo("dance")); // unchanged
    }
}
