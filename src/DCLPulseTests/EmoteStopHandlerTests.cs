using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

[TestFixture]
public class EmoteStopHandlerTests
{
    private const uint START_TIME = 5000;
    private const uint STOP_TIME = 6000;

    private EmoteBoard emoteBoard;
    private ITimeProvider timeProvider;
    private EmoteStopHandler handler;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        emoteBoard = new EmoteBoard(100);
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(STOP_TIME);

        handler = new EmoteStopHandler(emoteBoard, timeProvider, Substitute.For<ILogger<EmoteStopHandler>>());
        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [Test]
    public void Handle_ActiveEmote_StopsWithServerTick()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        emoteBoard.Start(peer, "dance", START_TIME, durationMs: null);

        handler.Handle(peers, peer, new ClientMessage { EmoteStop = new EmoteStop() });

        EmoteState? state = emoteBoard.Get(peer);
        Assert.That(state!.EmoteId, Is.Null);
        Assert.That(state.StopTick, Is.EqualTo(STOP_TIME));
        Assert.That(state.StopReason, Is.EqualTo(EmoteStopReason.Cancelled));
    }

    [Test]
    public void Handle_NoActiveEmote_DoesNothing()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);

        handler.Handle(peers, peer, new ClientMessage { EmoteStop = new EmoteStop() });

        Assert.That(emoteBoard.Get(peer), Is.Null);
    }

    [Test]
    public void Handle_UnauthenticatedPeer_SkipsProcessing()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.PENDING_AUTH);
        emoteBoard.Start(peer, "dance", START_TIME, durationMs: null);

        handler.Handle(peers, peer, new ClientMessage { EmoteStop = new EmoteStop() });

        Assert.That(emoteBoard.IsEmoting(peer), Is.True);
    }
}