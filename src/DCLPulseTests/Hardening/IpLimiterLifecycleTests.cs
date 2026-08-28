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
///     Verifies that the per-source-IP connection slot is returned at the
///     <see cref="PeersManager" /> lifecycle boundary. The transport thread reserves the slot and
///     binds it to the <see cref="PeerIndex" />; the owning worker is the only release path for a
///     peer that ever reached <c>OnPeerConnected</c>, so a missed release here leaks the IP's
///     budget for the lifetime of the process.
/// </summary>
[TestFixture]
public class IpLimiterLifecycleTests
{
    private const string IP = "203.0.113.1";
    private const string OTHER_IP = "203.0.113.2";

    private PeersManager manager;
    private IpLimiter limiter;
    private Channel<MessagePipe.IncomingEvent> eventChannel;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        ITimeProvider? timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(5000u);

        var snapshotBoard = new SnapshotBoard(100, 10);

        IOptionsMonitor<IpLimiterOptions> optionsMonitor = Substitute.For<IOptionsMonitor<IpLimiterOptions>>();
        optionsMonitor.CurrentValue.Returns(new IpLimiterOptions { Enabled = true, MaxConcurrency = 4 });
        optionsMonitor.OnChange(Arg.Any<Action<IpLimiterOptions, string?>>()).Returns(Substitute.For<IDisposable>());
        limiter = new IpLimiter(optionsMonitor, Substitute.For<ILogger<IpLimiter>>());

        manager = new PeersManager(
            new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters()),
            new PeerStateFactory(),
            Substitute.For<IAreaOfInterest>(),
            snapshotBoard,
            new RealmSpatialGrids(100, 100),
            new IdentityBoard(100),
            new PeerOptions(),
            Substitute.For<ILogger<PeersManager>>(),
            Substitute.For<ILogger<PeerSimulation>>(),
            timeProvider,
            new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>(),
            Substitute.For<ITransport>(),
            new ProfileBoard(100),
            new ClientMessageCounters(),
            new EmoteCompleter(snapshotBoard, timeProvider),
            Substitute.For<IPeerIndexAllocator>(),
            new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions
            {
                PreAuthBudget = 8,
                MaxConcurrentPreAuthPerIP = 4,
            })),
            limiter);

        eventChannel = Channel.CreateUnbounded<MessagePipe.IncomingEvent>();
        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [TearDown]
    public void TearDown()
    {
        manager.Dispose();
        limiter.Dispose();
    }

    [Test]
    public void Disconnect_ReleasesIpSlot()
    {
        var peer = new PeerIndex(1);

        // Transport thread reserves and commits the slot before announcing the peer.
        Assert.That(limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        limiter.Bind(peer, IP, ConnectionClass.PLAYER);
        Assert.That(limiter.TrackedIps, Is.EqualTo(1));

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peer));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peer));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(limiter.TrackedIps, Is.EqualTo(0),
            "The worker must return the peer's per-IP connection slot on the Disconnected lifecycle event");
    }

    [Test]
    public void Disconnect_FreesCapacityForTheSameIp()
    {
        const int CAP = 4;
        var peers4 = new PeerIndex[CAP];

        for (var i = 0; i < CAP; i++)
        {
            peers4[i] = new PeerIndex((uint)i);
            Assert.That(limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
            limiter.Bind(peers4[i], IP, ConnectionClass.PLAYER);
            eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(peers4[i]));
        }

        Assert.That(limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False, "The IP is at its cap before any disconnect");

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(peers4[0]));
        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
            "Draining one disconnect must free exactly one slot for that IP");
        Assert.That(limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False);
    }

    [Test]
    public void DisconnectDrainedTwice_ReleasesOnlyOnce()
    {
        var first = new PeerIndex(1);
        var second = new PeerIndex(2);

        limiter.TryAcquire(IP, ConnectionClass.PLAYER);
        limiter.Bind(first, IP, ConnectionClass.PLAYER);
        limiter.TryAcquire(IP, ConnectionClass.PLAYER);
        limiter.Bind(second, IP, ConnectionClass.PLAYER);

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Connected(first));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(first));
        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(first));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(limiter.TrackedIps, Is.EqualTo(1),
            "Release is idempotent — a repeated Disconnected event must not free the second peer's slot");
    }

    [Test]
    public void Disconnect_OfUnboundPeer_LeavesOtherReservationsIntact()
    {
        // A peer refused at the transport seam never reaches a worker, so its reservation is
        // handed back inline with Abandon and no binding exists to release here.
        limiter.TryAcquire(OTHER_IP, ConnectionClass.PLAYER);

        eventChannel.Writer.TryWrite(MessagePipe.IncomingEvent.Disconnected(new PeerIndex(42)));

        manager.DrainEvents(eventChannel.Reader, peers, workerIndex: 0);

        Assert.That(limiter.TrackedIps, Is.EqualTo(1),
            "Release is keyed by PeerIndex — an unbound peer decrements nothing");
    }
}
