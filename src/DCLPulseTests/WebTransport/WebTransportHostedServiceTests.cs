using DCL.WebTransport;
using Decentraland.Pulse;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;
using Pulse.Transport.WebTransport;
using System.Text;

namespace DCLPulseTests.WebTransport;

/// <summary>
///     Drives <see cref="WebTransportHostedService" /> through its internal <c>HandleEvent</c> /
///     <c>FlushOutgoing</c> seam with a recording fake host, exercising connect/admission,
///     stream reassembly, datagram staleness-drop, <see cref="PacketMode" /> → QUIC-primitive
///     mapping, the oversize-datagram drop, teardown, and the reused-<see cref="PeerIndex" />
///     reconnect path — all without the native library or the transport thread.
/// </summary>
[TestFixture]
public class WebTransportHostedServiceTests
{
    private const int MAX_PEERS = 4;
    private const byte CHANNEL_SEQUENCED = 1; // mirrors WebTransportHostedService.CHANNEL_SEQUENCED

    private RecordingWebTransportHost host;
    private MessagePipe messagePipe;
    private PeerIndexAllocator allocator;
    private IdentityBoard identityBoard;

    [SetUp]
    public void SetUp()
    {
        host = new RecordingWebTransportHost();

        messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10));
        allocator = new PeerIndexAllocator(MAX_PEERS);
        identityBoard = new IdentityBoard(MAX_PEERS);
    }

    [TearDown]
    public void TearDown() => host.Dispose();

    [Test]
    public async Task Connect_AdmittedPeer_AllocatesWebTransportStampedIndex_AndSignalsConnected()
    {
        WebTransportHostedService service = MakeService();

        service.HandleEvent(Connect(wtId: 42));

        MessagePipe.IncomingEvent connected = await DrainOneAsync();
        Assert.That(connected.LifeCycle, Is.EqualTo(MessagePipe.PeerEventType.Connected));
        Assert.That(connected.From.Transport, Is.EqualTo(TransportId.WebTransport),
            "the peer's index must be stamped WebTransport so its traffic routes back to this transport");
        Assert.That(allocator.FreeCount, Is.EqualTo(MAX_PEERS - 1));
        Assert.That(host.Disconnects, Is.Empty);
    }

    [Test]
    public void Connect_PoolExhausted_RefusesWithServerFull()
    {
        // A one-slot pool: the first peer takes it, the second finds the shared pool empty.
        WebTransportHostedService service = MakeService(maxPeers: 1);

        service.HandleEvent(Connect(wtId: 1));
        service.HandleEvent(Connect(wtId: 2));

        Assert.That(host.Disconnects, Has.Count.EqualTo(1));
        Assert.That(host.Disconnects[0], Is.EqualTo((2ul, (uint)DisconnectReason.SERVER_FULL)),
            "only the flooding peer is refused; the admitted peer is untouched");
        Assert.That(allocator.FreeCount, Is.EqualTo(0), "the single slot went to the admitted peer; the flood found the pool empty");
    }

    [Test]
    public void Connect_PreAuthIpLimitReached_RollsBackAllocation_AndRefuses()
    {
        // Per-IP cap of 1: the second connect from the same IP is refused and its slot returned.
        WebTransportHostedService service = MakeService(maxConcurrentPreAuthPerIp: 1);

        service.HandleEvent(Connect(wtId: 1, ip: "10.0.0.1:5000"));
        service.HandleEvent(Connect(wtId: 2, ip: "10.0.0.1:6000"));

        Assert.That(host.Disconnects, Has.Count.EqualTo(1));
        Assert.That(host.Disconnects[0], Is.EqualTo((2ul, (uint)DisconnectReason.PRE_AUTH_IP_LIMIT_EXHAUSTED)));
        Assert.That(allocator.FreeCount, Is.EqualTo(MAX_PEERS - 1),
            "the refused peer's PeerIndex must be rolled back to the pool");
    }

    [Test]
    public void StreamData_ReassemblesMultipleFramedMessages_DeliversEach()
    {
        WebTransportHostedService service = MakeService();
        service.HandleEvent(Connect(wtId: 7));

        // Two length-framed ClientMessages coalesced into one inbound stream chunk.
        byte[] chunk = Concat(
            StreamFraming.Frame(ClientMessageBytes(subjectId: 1)),
            StreamFraming.Frame(ClientMessageBytes(subjectId: 2)));

        service.HandleEvent(Stream(wtId: 7, chunk));

        // IncomingQueueDepth counts delivered data messages (lifecycle events aren't counted): both frames parsed.
        Assert.That(messagePipe.IncomingQueueDepth, Is.EqualTo(2));
    }

    [Test]
    public void StreamData_FrameSplitAcrossChunks_DeliversOnceComplete()
    {
        WebTransportHostedService service = MakeService();
        service.HandleEvent(Connect(wtId: 7));

        byte[] framed = StreamFraming.Frame(ClientMessageBytes(subjectId: 1));
        byte[] head = framed[..3];
        byte[] tail = framed[3..];

        service.HandleEvent(Stream(wtId: 7, head));
        Assert.That(messagePipe.IncomingQueueDepth, Is.EqualTo(0), "no full frame yet — nothing delivered");

        service.HandleEvent(Stream(wtId: 7, tail));
        Assert.That(messagePipe.IncomingQueueDepth, Is.EqualTo(1), "the completed frame is delivered once");
    }

    [Test]
    public void Datagram_SequencedChannel_DropsStaleAndDuplicate()
    {
        WebTransportHostedService service = MakeService();
        service.HandleEvent(Connect(wtId: 7));

        service.HandleEvent(Datagram(7, DatagramFraming.Frame(CHANNEL_SEQUENCED, 5, ClientMessageBytes(1)))); // accept
        service.HandleEvent(Datagram(7, DatagramFraming.Frame(CHANNEL_SEQUENCED, 3, ClientMessageBytes(1)))); // stale → drop
        service.HandleEvent(Datagram(7, DatagramFraming.Frame(CHANNEL_SEQUENCED, 6, ClientMessageBytes(1)))); // accept

        // Two datagrams accepted (seq 5, 6); seq 3 was stale and dropped before delivery.
        Assert.That(messagePipe.IncomingQueueDepth, Is.EqualTo(2));
    }

    [Test]
    public void Send_Reliable_WritesLengthFramedStream()
    {
        WebTransportHostedService service = MakeService();
        service.HandleEvent(Connect(wtId: 42));
        PeerIndex peer = new (0, TransportId.WebTransport); // first connect drew slot 0 from the fresh pool

        var response = new ServerMessage { Handshake = new HandshakeResponse { Success = true } };
        messagePipe.Send(new MessagePipe.OutgoingMessage(peer, response, PacketMode.RELIABLE));
        service.FlushOutgoing();

        Assert.That(host.Streams, Has.Count.EqualTo(1));
        Assert.That(host.Streams[0].Peer, Is.EqualTo(42ul));
        Assert.That(host.Streams[0].Data, Is.EqualTo(StreamFraming.Frame(response.ToByteArray())),
            "the reliable send is the length-framed message");
        Assert.That(host.Datagrams, Is.Empty);
    }

    [Test]
    public void Send_Unreliable_WritesRawDatagramPayload()
    {
        WebTransportHostedService service = MakeService();
        service.HandleEvent(Connect(wtId: 42));
        PeerIndex peer = new (0, TransportId.WebTransport);

        var msg = new ServerMessage { Handshake = new HandshakeResponse { Success = true } };
        messagePipe.Send(new MessagePipe.OutgoingMessage(peer, msg, PacketMode.UNRELIABLE_SEQUENCED));
        messagePipe.Send(new MessagePipe.OutgoingMessage(peer, msg, PacketMode.UNRELIABLE_UNSEQUENCED));
        service.FlushOutgoing();

        // Server→client datagrams carry no transport header — the client detects staleness from the
        // message body's own sequence, so each datagram is exactly the serialized ServerMessage.
        Assert.That(host.Datagrams, Has.Count.EqualTo(2));
        Assert.That(host.Datagrams[0].Data, Is.EqualTo(msg.ToByteArray()));
        Assert.That(host.Datagrams[1].Data, Is.EqualTo(msg.ToByteArray()), "both sequenced and unsequenced go raw on the server→client path");
    }

    [Test]
    public void Send_OversizeDatagram_IsDroppedNotRerouted()
    {
        // An unreliable message larger than the datagram cap is a server bug: it is dropped (and logged
        // as an error), NOT silently rerouted to the reliable stream — that would mask the regression
        // and turn an unreliable send into a head-of-line-blocking reliable one.
        WebTransportHostedService service = MakeService(maxDatagramBytes: 1);
        service.HandleEvent(Connect(wtId: 42));
        PeerIndex peer = new (0, TransportId.WebTransport);

        var msg = new ServerMessage { Handshake = new HandshakeResponse { Success = true } };
        messagePipe.Send(new MessagePipe.OutgoingMessage(peer, msg, PacketMode.UNRELIABLE_SEQUENCED));
        service.FlushOutgoing();

        Assert.That(host.Datagrams, Is.Empty);
        Assert.That(host.Streams, Is.Empty);
    }

    [Test]
    public void Send_Disconnect_ClosesSessionWithReason()
    {
        WebTransportHostedService service = MakeService();
        service.HandleEvent(Connect(wtId: 42));
        PeerIndex peer = new (0, TransportId.WebTransport);

        messagePipe.SendDisconnect(peer, DisconnectReason.BANNED);
        service.FlushOutgoing();

        Assert.That(host.Disconnects, Has.Count.EqualTo(1));
        Assert.That(host.Disconnects[0], Is.EqualTo((42ul, (uint)DisconnectReason.BANNED)));
    }

    [Test]
    public async Task Disconnect_TearsDownPeer_ParksSlot_AndSignalsDisconnected()
    {
        WebTransportHostedService service = MakeService();
        service.HandleEvent(Connect(wtId: 42));
        PeerIndex peer = (await DrainOneAsync()).From;

        service.HandleEvent(Disconnect(wtId: 42));

        MessagePipe.IncomingEvent disconnected = await DrainOneAsync();
        Assert.That(disconnected.LifeCycle, Is.EqualTo(MessagePipe.PeerEventType.Disconnected));
        Assert.That(disconnected.From, Is.EqualTo(peer));
        Assert.That(allocator.PendingCount, Is.EqualTo(1), "the slot is parked pending worker cleanup");

        // The peer is gone from the transport: a subsequent send is dropped, not forwarded.
        messagePipe.Send(new MessagePipe.OutgoingMessage(peer, new ServerMessage { Handshake = new HandshakeResponse() }, PacketMode.RELIABLE));
        service.FlushOutgoing();
        Assert.That(host.Streams, Is.Empty);
    }

    [Test]
    public async Task Connect_ReusesReleasedSlot_WithFreshWebTransportStamp()
    {
        // Reused-PeerIndex path: a slot freed by one WebTransport peer, once released by the worker
        // cleanup, comes back stamped WebTransport for the next session on the same slot value.
        WebTransportHostedService service = MakeService(maxPeers: 1);

        service.HandleEvent(Connect(wtId: 100));
        PeerIndex first = (await DrainOneAsync()).From;

        service.HandleEvent(Disconnect(wtId: 100));
        await DrainOneAsync(); // Disconnected
        allocator.Release(first); // simulate PeerSimulation.CleanupDisconnectedPeer

        service.HandleEvent(Connect(wtId: 200));
        MessagePipe.IncomingEvent reconnected = await DrainOneAsync();

        Assert.That(reconnected.From.Value, Is.EqualTo(first.Value), "the same slot value is reissued");
        Assert.That(reconnected.From.Transport, Is.EqualTo(TransportId.WebTransport));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private WebTransportHostedService MakeService(
        int maxPeers = MAX_PEERS,
        int maxDatagramBytes = 1200,
        int maxConcurrentPreAuthPerIp = 0)
    {
        if (maxPeers != MAX_PEERS)
            allocator = new PeerIndexAllocator(maxPeers);

        var options = Options.Create(new WebTransportOptions
        {
            MaxDatagramBytes = maxDatagramBytes,
            MaxMessageBytes = 4096,
        });

        var preAuth = new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions
        {
            MaxConcurrentPreAuthPerIP = maxConcurrentPreAuthPerIp,
            PreAuthBudget = 0,
        }));

        ITimeProvider timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(0u);
        var corruptedLimiter = new CorruptedPacketLimiter(
            Options.Create(new CorruptedPacketLimiterOptions { BurstCapacity = 5, MaxPerMinute = 60 }),
            timeProvider);

        return new WebTransportHostedService(host, options, Substitute.For<ILogger<WebTransportHostedService>>(),
            messagePipe, allocator, identityBoard, preAuth, corruptedLimiter);
    }

    private async Task<MessagePipe.IncomingEvent> DrainOneAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (MessagePipe.IncomingEvent e in messagePipe.ReadIncomingEventsAsync(cts.Token))
            return e;

        throw new InvalidOperationException("no incoming event");
    }

    private static WebTransportEvent Connect(ulong wtId, string ip = "1.2.3.4:5000") =>
        new (WebTransportEventKind.Connect, wtId, 0, Encoding.UTF8.GetBytes(ip));

    private static WebTransportEvent Stream(ulong wtId, byte[] data) =>
        new (WebTransportEventKind.StreamData, wtId, 0, data);

    private static WebTransportEvent Datagram(ulong wtId, byte[] data) =>
        new (WebTransportEventKind.Datagram, wtId, 0, data);

    private static WebTransportEvent Disconnect(ulong wtId) =>
        new (WebTransportEventKind.Disconnect, wtId, 0, Array.Empty<byte>());

    private static byte[] ClientMessageBytes(uint subjectId) =>
        new ClientMessage { Resync = new ResyncRequest { SubjectId = subjectId, KnownSeq = 0 } }.ToByteArray();

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    /// <summary>
    ///     Records sends and disconnects for assertion. A hand-written fake rather than an NSubstitute
    ///     mock because NSubstitute can't observe a <see cref="ReadOnlySpan{T}" /> argument (a ref struct
    ///     can't be boxed into its call recorder); the fake copies the span at call time.
    /// </summary>
    private sealed class RecordingWebTransportHost : IWebTransportHost
    {
        public List<(ulong Peer, byte[] Data)> Streams { get; } = new ();
        public List<(ulong Peer, byte[] Data)> Datagrams { get; } = new ();
        public List<(ulong Peer, uint Reason)> Disconnects { get; } = new ();

        // Tests drive HandleEvent directly, so the service never services this fake.
        public bool TryService(uint timeoutMs, out WebTransportEvent ev)
        {
            ev = default;
            return false;
        }

        public bool SendStream(ulong peerId, ReadOnlySpan<byte> data)
        {
            Streams.Add((peerId, data.ToArray()));
            return true;
        }

        public bool SendDatagram(ulong peerId, ReadOnlySpan<byte> data)
        {
            Datagrams.Add((peerId, data.ToArray()));
            return true;
        }

        public bool Disconnect(ulong peerId, uint reason)
        {
            Disconnects.Add((peerId, reason));
            return true;
        }

        public void Dispose() { }
    }
}
