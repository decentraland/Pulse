using System.Threading.Channels;
using Decentraland.Pulse;
using Google.Protobuf;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging;

/// <summary>
///     Receives raw transport packets from a transport thread, parses them off that thread,
///     and dispatches the resulting <see cref="ClientMessage" /> to <see cref="PeersManager" />;
///     and carries outgoing <see cref="ServerMessage" />s / disconnects back to the owning
///     transport. Two transports (ENet, WebTransport) can drive this concurrently.
///     Threading model:
///     Writers (transport threads) — call <see cref="OnDataReceived" /> / <see cref="OnPeerConnected" />:
///     parse off the wire, enqueue to the incoming channel (lock-free, never blocks).
///     Reader (the <see cref="PeersManager" /> router) — single async consumer; shards by peer.
///     Outgoing: worker threads call <see cref="Send" />; it routes to the owning transport's
///     channel, which that transport drains on its own loop.
/// </summary>
public sealed class MessagePipe(
    ILogger<MessagePipe> logger,
    ServerMessageCounters outgoingMessageCounters)
{
    // Multi-writer: ENet AND WebTransport threads enqueue lifecycle + data events.
    // SingleReader: the PeersManager router is the sole consumer.
    // Per-peer ordering still holds: each PeerIndex is owned by exactly one transport, which
    // enqueues that peer's events in order, and the router shards by PeerIndex so they reach a
    // single worker in arrival order. Ordering across different peers/transports doesn't matter.
    private readonly Channel<IncomingEvent> incomingChannel = Channel.CreateUnbounded<IncomingEvent>(
        new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

    // One outgoing channel per transport, indexed by the recipient's TransportId ((int)transport) —
    // which is why those enum values are kept contiguous from 0. Send() routes by the transport stamped
    // on the recipient's PeerIndex so each transport drains only its own peers' messages — a single
    // SingleReader channel can't serve two independent drainers (each TryRead removes the item for all
    // readers). Each channel is multi-writer (workers enqueue) / single-reader (the owning transport
    // drains). Unbounded: never blocks a sender; backlog shows up as memory growth, an observable signal.
    private readonly Channel<OutgoingMessage>[] outgoingChannels = CreateOutgoingChannels();

    // Atomic depth counters — SingleWriter/SingleReader channels don't support Reader.Count.
    private int incomingDepth;
    private int outgoingDepth;

    private static Channel<OutgoingMessage>[] CreateOutgoingChannels()
    {
        int transportCount = Enum.GetValues<TransportId>().Length;
        var channels = new Channel<OutgoingMessage>[transportCount];
        for (var i = 0; i < transportCount; i++)
            channels[i] = Channel.CreateUnbounded<OutgoingMessage>(
                new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });
        return channels;
    }

    public IAsyncEnumerable<IncomingEvent> ReadIncomingEventsAsync(CancellationToken ct) =>
        incomingChannel.Reader.ReadAllAsync(ct);

    /// <summary>Drain the next outgoing message for the ENet transport (the default channel).</summary>
    public bool TryReadOutgoingMessage(out OutgoingMessage message) =>
        TryReadOutgoingMessage(TransportId.ENet, out message);

    /// <summary>Drain the next outgoing message queued for <paramref name="transport" />.</summary>
    public bool TryReadOutgoingMessage(TransportId transport, out OutgoingMessage message)
    {
        if (!outgoingChannels[(int)transport].Reader.TryRead(out message))
            return false;

        Interlocked.Decrement(ref outgoingDepth);
        return true;
    }

    public int IncomingQueueDepth => Volatile.Read(ref incomingDepth);

    public int OutgoingQueueDepth => Volatile.Read(ref outgoingDepth);

    public void Send(OutgoingMessage message)
    {
        // Route by the transport stamped on the recipient's PeerIndex (set by the owning transport
        // service at connect). The stamp rides on every OutgoingMessage.To, so no side registry is
        // needed. A default-constructed index is TransportId.ENet, so legacy/test peers route to the
        // ENet channel exactly as before this became multi-transport.
        outgoingChannels[(int)message.To.Transport].Writer.TryWrite(message);
        Interlocked.Increment(ref outgoingDepth);

        // Disconnect envelopes carry a sentinel Message — skip the message-type counter.
        if (!message.IsDisconnect)
            outgoingMessageCounters.Increment(message.Message.MessageCase);
    }

    /// <summary>
    ///     Enqueues a disconnect for <paramref name="to" /> on the owning transport's outgoing
    ///     channel so that transport performs the actual disconnect. Callable from any thread.
    ///     The disconnect is ordered against in-flight sends to the same peer: both flow through
    ///     that peer's transport channel, FIFO.
    /// </summary>
    public void SendDisconnect(PeerIndex to, DisconnectReason reason) =>
        Send(OutgoingMessage.DisconnectPeer(to, reason));

    /// <summary>
    ///     Called on a transport thread for every received packet.
    ///     Must be fast and must not throw — any exception here stalls that transport's loop.
    ///     Returns <c>true</c> when the packet parsed and was enqueued; <c>false</c> when
    ///     protobuf parsing failed (the caller decides whether to disconnect — typically by
    ///     routing the event through <see cref="Pulse.Transport.Hardening.CorruptedPacketLimiter" />).
    /// </summary>
    public bool OnDataReceived(MessagePacket packet)
    {
        ClientMessage? message = null;

        try { message = ClientMessage.Parser.ParseFrom(packet.Data); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to parse packet from peer {PeerIndex}", packet.FromPeer.Value); }

        if (message is null)
            return false;

        incomingChannel.Writer.TryWrite(new IncomingEvent(packet.FromPeer, message));
        Interlocked.Increment(ref incomingDepth);
        return true;
    }

    /// <summary>
    ///     Called by the router when it reads an event from the incoming channel.
    /// </summary>
    public void AckIncomingRead() =>
        Interlocked.Decrement(ref incomingDepth);

    public void OnPeerConnected(PeerIndex peerIndex) =>
        incomingChannel.Writer.TryWrite(IncomingEvent.Connected(peerIndex));

    public void OnPeerDisconnected(PeerIndex peerIndex) =>
        incomingChannel.Writer.TryWrite(IncomingEvent.Disconnected(peerIndex));

    /// <summary>
    ///     Unified event written by a transport thread. Carries either a lifecycle event or a parsed message.
    ///     Per peer, Connect always precedes the first Receive (one transport owns the peer and enqueues in order).
    /// </summary>
    public readonly record struct IncomingEvent(PeerIndex From, ClientMessage? Message, PeerEventType? LifeCycle)
    {
        public IncomingEvent(PeerIndex from, ClientMessage message) : this(from, message, null) { }

        public static IncomingEvent Connected(PeerIndex from) =>
            new (from, null, PeerEventType.Connected);

        public static IncomingEvent Disconnected(PeerIndex from) =>
            new (from, null, PeerEventType.Disconnected);

        public bool IsLifeCycle => LifeCycle.HasValue;
    }

    /// <summary>
    ///     A unit of work for the owning transport's drain loop. Either a data send
    ///     (<see cref="Disconnect" /> null) or a disconnect request (<see cref="Disconnect" />
    ///     set, <see cref="Message" /> is a sentinel that must not be read). The union lets
    ///     workers post both through one channel so the transport sees them in enqueue order —
    ///     the peer's final reliable sends always reach the transport before the disconnect.
    ///     Consumers must check <see cref="IsDisconnect" /> before reading <see cref="Message" />.
    /// </summary>
    public readonly record struct OutgoingMessage(
        PeerIndex To,
        ServerMessage Message,
        PacketMode PacketMode,
        DisconnectReason? Disconnect = null)
    {
        public bool IsDisconnect => Disconnect.HasValue;

        public static OutgoingMessage DisconnectPeer(PeerIndex to, DisconnectReason reason) =>
            // null! — Message is a sentinel for disconnects and is never read; drain loops
            // branch on IsDisconnect first. Keeps the type non-nullable so data-send
            // consumers don't have to null-check on every access.
            new (to, null!, default, reason);
    }

    public enum PeerEventType { Connected, Disconnected }
}
