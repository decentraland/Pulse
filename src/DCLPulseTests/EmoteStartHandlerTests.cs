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
public class EmoteStartHandlerTests
{
    private const uint MONOTONIC_TIME = 5000;

    private EmoteBoard emoteBoard;
    private SnapshotBoard snapshotBoard;
    private ITimeProvider timeProvider;
    private EmoteStartHandler handler;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        emoteBoard = new EmoteBoard(100);
        snapshotBoard = new SnapshotBoard(100, 16);
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(MONOTONIC_TIME);

        handler = new EmoteStartHandler(emoteBoard, snapshotBoard, timeProvider, Substitute.For<ILogger<EmoteStartHandler>>());
        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [Test]
    public void Handle_AuthenticatedPeer_StartsEmoteWithServerTick()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);

        var message = new ClientMessage { EmoteStart = new EmoteStart { EmoteId = "wave", DurationMs = 3000 } };

        handler.Handle(peers, peer, message);

        EmoteState? state = emoteBoard.Get(peer);
        Assert.That(state, Is.Not.Null);
        Assert.That(state!.EmoteId, Is.EqualTo("wave"));
        Assert.That(state.StartTick, Is.EqualTo(MONOTONIC_TIME));
        Assert.That(state.DurationMs, Is.EqualTo(3000u));
    }

    [Test]
    public void Handle_AuthenticatedPeer_LoopingEmote_StoresNullDuration()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);

        var message = new ClientMessage { EmoteStart = new EmoteStart { EmoteId = "dance" } };

        handler.Handle(peers, peer, message);

        EmoteState? state = emoteBoard.Get(peer);
        Assert.That(state!.DurationMs, Is.Null);
    }

    [Test]
    public void Handle_UnauthenticatedPeer_SkipsProcessing()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.PENDING_AUTH);

        var message = new ClientMessage { EmoteStart = new EmoteStart { EmoteId = "wave", DurationMs = 3000 } };

        handler.Handle(peers, peer, message);

        Assert.That(emoteBoard.Get(peer), Is.Null);
    }

    [Test]
    public void Handle_UnknownPeer_SkipsProcessing()
    {
        var peer = new PeerIndex(1);

        var message = new ClientMessage { EmoteStart = new EmoteStart { EmoteId = "wave", DurationMs = 3000 } };

        handler.Handle(peers, peer, message);

        Assert.That(emoteBoard.Get(peer), Is.Null);
    }

    [Test]
    public void Handle_BumpsSnapshotSeq_WhenSnapshotExists()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);

        snapshotBoard.SetActive(peer);
        var initial = new PeerSnapshot(Seq: 5, ServerTick: 4000, Parcel: 0, default(Vector3), default(Vector3), default(Vector3), 0f, 0, 0f, 0f, null, null, default(PlayerAnimationFlags), default(GlideState));
        snapshotBoard.Publish(peer, initial);

        var message = new ClientMessage { EmoteStart = new EmoteStart { EmoteId = "wave", DurationMs = 3000 } };

        handler.Handle(peers, peer, message);

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot updated), Is.True);
        Assert.That(updated.Seq, Is.GreaterThan(initial.Seq));
        Assert.That(updated.ServerTick, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void Handle_DoesNotBumpSeq_WhenNoSnapshotExists()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);

        var message = new ClientMessage { EmoteStart = new EmoteStart { EmoteId = "wave", DurationMs = 3000 } };

        handler.Handle(peers, peer, message);

        Assert.That(snapshotBoard.TryRead(peer, out _), Is.False);
    }
}
