using Decentraland.Pulse;
using System.Threading.Channels;

namespace PulseTestClient.Networking;

/// <summary>
/// Receives raw transport packets from the transport thread, parses them off that thread,
/// and dispatches the resulting message to any handler.
/// </summary>
public sealed class MessagePipe
{
    private readonly Channel<IncomingMessage> incomingChannel = Channel.CreateUnbounded<IncomingMessage>(
        new UnboundedChannelOptions {SingleWriter = true, SingleReader = true});

    private readonly Channel<OutgoingMessage> outgoingChannel = Channel.CreateUnbounded<OutgoingMessage>(
        new UnboundedChannelOptions {SingleWriter = true, SingleReader = true});

    public IAsyncEnumerable<IncomingMessage> ReadIncomingMessagesAsync(CancellationToken ct) =>
        incomingChannel.Reader.ReadAllAsync(ct);

    public IAsyncEnumerable<OutgoingMessage> ReadOutgoingMessagesAsync(CancellationToken ct) =>
        outgoingChannel.Reader.ReadAllAsync(ct);

    public bool TryReadOutgoingMessage(out OutgoingMessage message) =>
        outgoingChannel.Reader.TryRead(out message);

    public void Send(OutgoingMessage message) =>
        outgoingChannel.Writer.TryWrite(message);

    /// <summary>
    ///     Called on the Transport thread for every received packet.
    ///     Must be fast and must not throw — any exception here stalls the Transport loop.
    /// </summary>
    public void OnDataReceived<TTransportPacket>(MessagePacket<TTransportPacket> packet)
        where TTransportPacket : IDisposable
    {
        ServerMessage? message = null;

        try
        {
            message = ServerMessage.Parser.ParseFrom(packet.Data);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        if (message is null)
            return;

        incomingChannel.Writer.TryWrite(new IncomingMessage(packet.FromPeer, message));
    }

    public readonly struct IncomingMessage
    {
        public PeerId From { get; }
        public ServerMessage Message { get; }

        public IncomingMessage(PeerId from, ServerMessage message)
        {
            From = from;
            Message = message;
        }
    }

    public readonly struct OutgoingMessage
    {
        public ClientMessage Message { get; }
        public ITransport.PacketMode PacketMode { get; }

        public OutgoingMessage(ClientMessage message, ITransport.PacketMode packetMode)
        {
            Message = message;
            PacketMode = packetMode;
        }
    }
}