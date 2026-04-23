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

namespace DCLPulseTests.Hardening;

/// <summary>
///     Verifies the admission release semantics at the <see cref="PeersManager" /> lifecycle
///     boundary. The worker drives every decrement — ENet-thread Disconnect no longer touches
///     the admission counters. The idempotency contract: releasing on Disconnect after a
///     promotion is a no-op.
/// </summary>
[TestFixture]
public class PreAuthAdmissionLifecycleTests
{
    private PeersManager manager;
    private PreAuthAdmission admission;
    private Channel<MessagePipe.IncomingEvent> eventChannel;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        ITimeProvider? timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(5000u);

        var snapshotBoard = new SnapshotBoard(100, 10);

        admission = new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions
        {
            PreAuthBudget = 8,
            MaxConcurrentPreAuthPerIP = 4,
        }));

        manager = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10)),
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
            new ClientMessageCounters(8),
            new EmoteCompleter(snapshotBoard, timeProvider),
            Substitute.For<IPeerIndexAllocator>(),
            admission);

        eventChannel = Channel.CreateUnbounded<MessagePipe.IncomingEvent>();
        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [TearDown]
    public void TearDown()
    {
        manager.Dispose();
    }

    [Test]
    public void Disconnect_WhilePendingAuth_ReleasesAdmission()
    {
        var peer = new PeerIndex(1);

        // ENet thread admits, then worker processes lifecycle.
        admission.TryAdmit(peer, "203.0.113.1");
        Assert.That(admission.InFlight, Is.EqualTo(1));

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peer));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peer));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(admission.InFlight, Is.EqualTo(0),
            "Disconnect on a PENDING_AUTH peer must release the admission slot");
    }

    [Test]
    public void Disconnect_AfterPromotion_IsNoOp()
    {
        var peer = new PeerIndex(2);

        // Peer admitted, then handshake promoted it — admission already released.
        admission.TryAdmit(peer, "203.0.113.1");
        admission.ReleaseOnPromotion(peer);
        Assert.That(admission.InFlight, Is.EqualTo(0));

        // A second peer holds one in-flight slot so we can detect any incorrect decrement.
        admission.TryAdmit(new PeerIndex(99), "203.0.113.2");

        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peer));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(admission.InFlight, Is.EqualTo(1),
            "Disconnecting an already-promoted peer must not decrement — the promotion path already did");
    }
}
