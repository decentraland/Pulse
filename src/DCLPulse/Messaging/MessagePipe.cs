using System.Threading.Channels;
using Decentraland.Pulse;
using Pulse.Peers;

namespace Pulse.Messaging;

/// <summary>
///     Receives raw transport packets from the ENet thread, parses them off that thread,
///     and dispatches the resulting <see cref="ClientMessage" /> to <see cref="PeersManager" />.
///     Threading model:
///     Writer (ENet thread) — calls <see cref="OnDataReceived{TTransportPacket}" />:
///     1. Parses protobuf synchronously from native memory (microseconds, no alloc for the span).
///     2. Disposes the native ENet packet immediately so ENet can reclaim its memory.
///     3. Writes the parsed envelope to an unbounded channel (lock-free, never blocks).
///     Reader (this BackgroundService) — single async consumer:
///     Dispatches to <see cref="PeersManager" /> sequentially, preserving per-peer ordering.
/// </summary>
public sealed class MessagePipe(ILogger<MessagePipe> logger)
{
    public readonly record struct IncomingMessage(PeerId From, ClientMessage Message);

    // SingleWriter=true: only the ENet thread enqueues.
    // SingleReader=true: only our ExecuteAsync loop dequeues.
    // Unbounded: never blocks the ENet thread. Back-pressure shows up as memory growth,
    // which is an observable signal if processing falls behind.
    private readonly Channel<IncomingMessage> channel = Channel.CreateUnbounded<IncomingMessage>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

    public IAsyncEnumerable<IncomingMessage> ReadMessagesAsync(CancellationToken ct) =>
        channel.Reader.ReadAllAsync(ct);

    /// <summary>
    ///     Called on the Transport thread for every received packet.
    ///     Must be fast and must not throw — any exception here stalls the Transport loop.
    /// </summary>
    public void OnDataReceived<TTransportPacket>(MessagePacket<TTransportPacket> packet)
        where TTransportPacket: IDisposable
    {
        ClientMessage? message = null;

        try { message = ClientMessage.Parser.ParseFrom(packet.Data); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to parse packet from peer {PeerId}", packet.FromPeer.Value); }
        finally
        {
            // Release native ENet memory regardless of parse outcome.
            packet.Dispose();
        }

        if (message is null)
            return;

        // TryWrite on an unbounded channel always succeeds.
        channel.Writer.TryWrite(new IncomingMessage(packet.FromPeer, message));
    }
}
