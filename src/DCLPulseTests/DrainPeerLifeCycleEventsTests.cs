using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Threading.Channels;

namespace DCLPulseTests;

[TestFixture]
public class DrainPeerLifeCycleEventsTests
{
    private const uint MONOTONIC_TIME = 5000;

    private PeersManager manager;
    private Channel<MessagePipe.PeerLifeCycleEvent> lifeCycleChannel;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        ITimeProvider? timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(MONOTONIC_TIME);

        var snapshotBoard = new SnapshotBoard(100, 10);

        manager = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>()),
            new PeerStateFactory(),
            Substitute.For<IAreaOfInterest>(),
            snapshotBoard,
            new SpatialGrid(100, 100),
            new IdentityBoard(100),
            new PeerOptions(),
            Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(),
            timeProvider,
            new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>(),
            Substitute.For<ITransport>(),
            new ProfileBoard(100),
            new EmoteBoard(100));

        lifeCycleChannel = Channel.CreateUnbounded<MessagePipe.PeerLifeCycleEvent>();
        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [TearDown]
    public void TearDown()
    {
        manager.Dispose();
    }

    [Test]
    public void ConnectedEvent_AddsPeerAsPendingAuth()
    {
        var peerIndex = new PeerIndex(1);
        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peerIndex, MessagePipe.PeerEventType.Connected));

        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers.ContainsKey(peerIndex), Is.True);
        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
    }

    [Test]
    public void ConnectedEvent_SetsTransportStateWithCurrentTime()
    {
        var peerIndex = new PeerIndex(1);
        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peerIndex, MessagePipe.PeerEventType.Connected));

        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        PeerTransportState transport = peers[peerIndex].TransportState;
        Assert.That(transport.ConnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void DisconnectedEvent_SetsTransportStateWithCurrentTime()
    {
        var peerIndex = new PeerIndex(1);
        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peerIndex, MessagePipe.PeerEventType.Disconnected));

        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        PeerTransportState transport = peers[peerIndex].TransportState;
        Assert.That(transport.DisconnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void DisconnectedEvent_ExistingPeer_TransitionsToDisconnecting()
    {
        var peerIndex = new PeerIndex(1);

        // Simulate an already-connected peer
        var existingState = new PeerState(PeerConnectionState.AUTHENTICATED);
        peers[peerIndex] = existingState;

        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peerIndex, MessagePipe.PeerEventType.Disconnected));

        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.DISCONNECTING));
        Assert.That(peers[peerIndex].TransportState.DisconnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void DisconnectedEvent_UnknownPeer_CreatesStateAsDisconnecting()
    {
        var peerIndex = new PeerIndex(99);

        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peerIndex, MessagePipe.PeerEventType.Disconnected));

        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers.ContainsKey(peerIndex), Is.True);
        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.DISCONNECTING));
        Assert.That(peers[peerIndex].TransportState.DisconnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void ConnectThenDisconnect_SamePeer_FinalStateIsDisconnecting()
    {
        var peerIndex = new PeerIndex(1);

        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peerIndex, MessagePipe.PeerEventType.Connected));
        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peerIndex, MessagePipe.PeerEventType.Disconnected));

        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.DISCONNECTING));
        Assert.That(peers[peerIndex].TransportState.DisconnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void MultipleConnectedEvents_AllPeersStored()
    {
        var peer0 = new PeerIndex(0);
        var peer1 = new PeerIndex(1);
        var peer2 = new PeerIndex(2);

        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peer0, MessagePipe.PeerEventType.Connected));
        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peer1, MessagePipe.PeerEventType.Connected));
        lifeCycleChannel.Writer.TryWrite(new MessagePipe.PeerLifeCycleEvent(peer2, MessagePipe.PeerEventType.Connected));

        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers, Has.Count.EqualTo(3));
        Assert.That(peers[peer0].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
        Assert.That(peers[peer1].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
        Assert.That(peers[peer2].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
    }

    [Test]
    public void EmptyChannel_NoPeersModified()
    {
        manager.DrainPeerLifeCycleEvents(lifeCycleChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers, Is.Empty);
    }
}
