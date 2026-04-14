using System.Threading.Channels;
using Decentraland.Pulse;
using Google.Protobuf;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging;

/// <summary>
///     Receives raw transport packets from the ENet thread, parses them off that thread,
///     and dispatches the resulting <see cref="ClientMessage" /> to <see cref="PeersManager" />.
///     Threading model:
///     Writer (ENet thread) — calls <see cref="OnDataReceived" />:
///     1. Parses protobuf synchronously from native memory (microseconds, no alloc for the span).
///     2. Disposes the native ENet packet immediately so ENet can reclaim its memory.
///     3. Writes the parsed envelope to an unbounded channel (lock-free, never blocks).
///     Reader (this BackgroundService) — single async consumer:
///     Dispatches to <see cref="PeersManager" /> sequentially, preserving per-peer ordering.
/// </summary>
public sealed class MessagePipe(
    ILogger<MessagePipe> logger,
    ServerMessageCounters outgoingMessageCounters)
{
    // SingleWriter=true: only the ENet thread enqueues both lifecycle events and data messages.
    // SingleReader=true: only the router dequeues.
    // Unified channel preserves ENet's event ordering: Connect always precedes the first Receive.
    private readonly Channel<IncomingEvent> incomingChannel = Channel.CreateUnbounded<IncomingEvent>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

    // SingleWriter=false: worker threads enqueue.
    // SingleReader=true: only the ENet thread dequeues.
    // Unbounded: never blocks the ENet thread. Back-pressure shows up as memory growth,
    // which is an observable signal if processing falls behind.
    private readonly Channel<OutgoingMessage> outgoingChannel = Channel.CreateUnbounded<OutgoingMessage>(
        new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

    // Atomic depth counters — SingleWriter/SingleReader channels don't support Reader.Count.
    private int incomingDepth;
    private int outgoingDepth;

    public IAsyncEnumerable<IncomingEvent> ReadIncomingEventsAsync(CancellationToken ct) =>
        incomingChannel.Reader.ReadAllAsync(ct);

    public IAsyncEnumerable<OutgoingMessage> ReadOutgoingMessagesAsync(CancellationToken ct) =>
        outgoingChannel.Reader.ReadAllAsync(ct);

    public bool TryReadOutgoingMessage(out OutgoingMessage message)
    {
        if (!outgoingChannel.Reader.TryRead(out message))
            return false;

        Interlocked.Decrement(ref outgoingDepth);
        return true;
    }

    public int IncomingQueueDepth => Volatile.Read(ref incomingDepth);

    public int OutgoingQueueDepth => Volatile.Read(ref outgoingDepth);

    public void Send(OutgoingMessage message)
    {
        outgoingChannel.Writer.TryWrite(message);
        Interlocked.Increment(ref outgoingDepth);
        outgoingMessageCounters.Increment(message.Message.MessageCase);
    }

    /// <summary>
    ///     Called on the Transport thread for every received packet.
    ///     Must be fast and must not throw — any exception here stalls the Transport loop.
    /// </summary>
    public void OnDataReceived(MessagePacket packet)
    {
        ClientMessage? message = null;

        try { message = ClientMessage.Parser.ParseFrom(packet.Data); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to parse packet from peer {PeerIndex}", packet.FromPeer.Value); }

        if (message is null)
            return;

        incomingChannel.Writer.TryWrite(new IncomingEvent(packet.FromPeer, message));
        Interlocked.Increment(ref incomingDepth);
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
    ///     Unified event written by the ENet thread. Carries either a lifecycle event or a parsed message.
    ///     Single channel preserves ENet's ordering: Connect always precedes the first Receive for a peer.
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

    public readonly record struct OutgoingMessage(PeerIndex To, ServerMessage Message, PacketMode PacketMode);

    public enum PeerEventType { Connected, Disconnected }
}
