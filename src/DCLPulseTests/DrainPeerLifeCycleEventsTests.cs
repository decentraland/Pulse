using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;
using System.Threading.Channels;

namespace DCLPulseTests;

[TestFixture]
public class DrainPeerLifeCycleEventsTests
{
    private const uint MONOTONIC_TIME = 5000;

    private PeersManager manager;
    private Channel<MessagePipe.IncomingEvent> eventChannel;
    private Dictionary<PeerIndex, PeerState> peers;
    private IdentityBoard identityBoard;

    [SetUp]
    public void SetUp()
    {
        ITimeProvider? timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(MONOTONIC_TIME);

        var snapshotBoard = new SnapshotBoard(100, 10);
        identityBoard = new IdentityBoard(100);

        manager = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10)),
            new PeerStateFactory(),
            Substitute.For<IAreaOfInterest>(),
            snapshotBoard,
            new SpatialGrid(100, 100),
            identityBoard,
            new PeerOptions(),
            Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(),
            timeProvider,
            new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>(),
            Substitute.For<ITransport>(),
            new ProfileBoard(100),
            new ClientMessageCounters(8),
            new EmoteCompleter(snapshotBoard, timeProvider),
            Substitute.For<IPeerIndexAllocator>(),
            new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions
            {
                PreAuthBudget = 0, MaxConcurrentPreAuthPerIP = 0,
            })));

        eventChannel = Channel.CreateUnbounded<MessagePipe.IncomingEvent>();
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
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peerIndex));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers.ContainsKey(peerIndex), Is.True);
        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
    }

    [Test]
    public void ConnectedEvent_SetsTransportStateWithCurrentTime()
    {
        var peerIndex = new PeerIndex(1);
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peerIndex));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        PeerTransportState transport = peers[peerIndex].TransportState;
        Assert.That(transport.ConnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void DisconnectedEvent_SetsTransportStateWithCurrentTime()
    {
        var peerIndex = new PeerIndex(1);
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peerIndex));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

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

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peerIndex));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.DISCONNECTING));
        Assert.That(peers[peerIndex].TransportState.DisconnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void DisconnectedEvent_UnknownPeer_CreatesStateAsDisconnecting()
    {
        var peerIndex = new PeerIndex(99);

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peerIndex));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers.ContainsKey(peerIndex), Is.True);
        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.DISCONNECTING));
        Assert.That(peers[peerIndex].TransportState.DisconnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void ConnectThenDisconnect_SamePeer_FinalStateIsDisconnecting()
    {
        var peerIndex = new PeerIndex(1);

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peerIndex));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peerIndex));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers[peerIndex].ConnectionState, Is.EqualTo(PeerConnectionState.DISCONNECTING));
        Assert.That(peers[peerIndex].TransportState.DisconnectionTime, Is.EqualTo(MONOTONIC_TIME));
    }

    [Test]
    public void MultipleConnectedEvents_AllPeersStored()
    {
        var peer0 = new PeerIndex(0);
        var peer1 = new PeerIndex(1);
        var peer2 = new PeerIndex(2);

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peer0));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peer1));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peer2));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers, Has.Count.EqualTo(3));
        Assert.That(peers[peer0].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
        Assert.That(peers[peer1].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
        Assert.That(peers[peer2].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));
    }

    [Test]
    public void EmptyChannel_NoPeersModified()
    {
        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers, Is.Empty);
    }

    /// <summary>
    ///     Pins the worker-shard isolation rule (<c>CLAUDE.md</c>): even when
    ///     <see cref="IdentityBoard" /> reports a wallet for a <see cref="PeerIndex" />, the
    ///     worker must NOT materialize a <see cref="PeerState" /> on the fly when it sees a
    ///     message for that index without a prior <c>Connected</c> lifecycle event. Doing so
    ///     would be cross-worker adoption, which was deliberately removed — the rule says all
    ///     coordination must go through the ENet → router → worker channel path, and state
    ///     must never be conjured by one worker on another's behalf.
    /// </summary>
    [Test]
    public void UnknownPeerMessage_DoesNotAdoptEvenWhenWalletIsInIdentityBoard()
    {
        var unknownPeer = new PeerIndex(42);

        // Simulate the state a cross-worker adoption scheme would rely on: another worker has
        // already authenticated the peer and the shared IdentityBoard reflects that.
        identityBoard.Set(unknownPeer, "0xCROSS_WORKER_WALLET");

        // A data message for this PeerIndex arrives on this worker with no Connected event first.
        eventChannel.Writer.TryWrite(
            new MessagePipe.IncomingEvent(unknownPeer, new ClientMessage { Input = new PlayerStateInput() }));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(peers, Does.Not.ContainKey(unknownPeer),
            "PeersManager must NOT materialize PeerState for an unknown PeerIndex when a message arrives — "
          + "cross-worker adoption is forbidden by the worker-shard isolation rule");
    }
}
