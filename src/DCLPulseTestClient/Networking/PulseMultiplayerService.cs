using Decentraland.Pulse;
using Google.Protobuf;

namespace PulseTestClient.Networking;

public partial class PulseMultiplayerService(
    ITransport transport,
    MessagePipe pipe) : IDisposable
{
    private readonly Dictionary<ServerMessage.MessageOneofCase, ISubscriber> subscribers = new();
    private CancellationTokenSource? connectionLifeCycleCts;

    public void Dispose()
    {
        connectionLifeCycleCts.SafeCancelAndDispose();
        transport.Dispose();
    }

    public async Task ConnectAsync(string address, int port, string authChain, CancellationToken ct)
    {
        connectionLifeCycleCts.SafeCancelAndDispose();
        connectionLifeCycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        await transport.ConnectAsync(address, port, ct);

        _ = RouteIncomingMessagesAsync(connectionLifeCycleCts.Token);

        pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
        {
            Handshake = new HandshakeRequest
            {
                AuthChain = ByteString.CopyFromUtf8(authChain),
            },
        }, ITransport.PacketMode.RELIABLE));

        await foreach (HandshakeResponse response in SubscribeAsync<HandshakeResponse>(
                           ServerMessage.MessageOneofCase.Handshake, ct))
        {
            if (!response.Success)
            {
                connectionLifeCycleCts.SafeCancelAndDispose();
                await transport.DisconnectAsync(ITransport.DisconnectReason.AuthFailed, ct);

                throw new PulseException(response.HasError ? response.Error : "Handshake failed");
            }

            break;
        }
    }

    public IAsyncEnumerable<T> SubscribeAsync<T>(ServerMessage.MessageOneofCase type, CancellationToken ct)
        where T : class
    {
        var subscriber = new GenericSubscriber<T>(type);

        subscribers.Add(type, subscriber);

        return subscriber.Channel.Reader.ReadAllAsync(ct);
    }

    private async Task RouteIncomingMessagesAsync(CancellationToken ct)
    {
        await foreach (MessagePipe.IncomingMessage message in pipe.ReadIncomingMessagesAsync(ct))
        {
            if (!subscribers.TryGetValue(message.Message.MessageCase, out ISubscriber? subscriber)) continue;
            subscriber.TryNotify(message.Message);
        }
    }
}