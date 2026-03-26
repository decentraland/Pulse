using Google.Protobuf;

namespace PulseTestClient.Networking;

public sealed class ENetTransport(
    ENetTransportOptions options,
    MessagePipe messagePipe) : ITransport
{
    private readonly byte[] receiveBuffer = new byte[4096];
    private readonly byte[] sendBuffer = new byte[4096];
    
    private Peer? serverPeer;
    private Host? client;
    private CancellationTokenSource? lifeCycleCts;
    
    public void Dispose()
    {
        serverPeer = null;
        client?.Flush();
        client?.Dispose();
        client = null;
        Library.Deinitialize();
    }

    public Task ConnectAsync(string ip, int port, CancellationToken ct)
    {
        if (!Library.Initialize())
            throw new InvalidOperationException("ENet library failed to initialize.");

        client = new Host();
        Address address = new Address();
        address.SetHost(ip);
        address.Port = (ushort) port;
        client.Create(peerLimit: 1, channelLimit: ENetChannel.COUNT);
        
        serverPeer = client.Connect(address, channelLimit: ENetChannel.COUNT);

        lifeCycleCts = lifeCycleCts.SafeRestartLinked(ct);
        ListenForIncomingDataAsync(ct);

        return Task.Run(async () =>
        {
            while (serverPeer?.State != PeerState.Connected)
                await Task.Delay(100, ct);
        }, ct);
    }

    public Task DisconnectAsync(ITransport.DisconnectReason reason, CancellationToken ct)
    {
        serverPeer?.Disconnect((uint) reason);
        return Task.CompletedTask;
    }

    public void Send(IMessage message, ITransport.PacketMode mode)
    {
        ENetChannel channel = ToENetChannel(mode);

        if (serverPeer != null)
            SendToPeer(serverPeer.Value, channel, message);
    }
    
    private Task ListenForIncomingDataAsync(CancellationToken ct)
    {
        // ENet must be driven on a single dedicated thread
        return Task.Factory.StartNew(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                var polled = false;

                while (!polled)
                {
                    if (client == null) continue;
                    
                    if (client.CheckEvents(out Event netEvent) <= 0)
                    {
                        if (client.Service(options.ServiceTimeoutMs, out netEvent) <= 0)
                            break;

                        polled = true;
                    }

                    ReceiveIncomingMessage(ref netEvent);
                }

                SendOutgoingMessages();
            }

            // Ensure any final outgoing packets are sent, such as disconnected notifications or any last moment data
            client?.Flush();
            client?.Dispose();
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ReceiveIncomingMessage(ref Event netEvent)
    {
        var peerId = new PeerId(netEvent.Peer.ID);

        switch (netEvent.Type)
        {
            case EventType.Connect:
                serverPeer = netEvent.Peer;
                break;

            case EventType.Disconnect:
                serverPeer = null;
                lifeCycleCts.SafeCancelAndDispose();
                break;

            case EventType.Timeout:
                serverPeer = null;
                lifeCycleCts.SafeCancelAndDispose();
                break;

            case EventType.Receive:
            {
                using Packet packet = netEvent.Packet;
                packet.CopyTo(receiveBuffer);

                messagePipe.OnDataReceived(new MessagePacket<Packet>(
                    packet,
                    new ReadOnlySpan<byte>(receiveBuffer, 0, packet.Length),
                    peerId));

                break;
            }
        }
    }

    /// <summary>
    ///     ENet is not thread-safe so we are obliged to write from the same thread we read
    /// </summary>
    private void SendOutgoingMessages()
    {
        while (messagePipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg))
        {
            ENetChannel channel = ToENetChannel(msg.PacketMode);

            if (serverPeer != null)
                SendToPeer(serverPeer.Value, channel, msg.Message);
        }
    }

    private void SendToPeer(Peer peer, ENetChannel channel, IMessage message)
    {
        var size = message.CalculateSize();
        var span = new Span<byte>(sendBuffer, 0, size);
        message.WriteTo(span);
        var packet = default(Packet);
        packet.Create(span, channel.PacketMode);
        peer.Send(channel.ChannelId, ref packet);
    }

    private static ENetChannel ToENetChannel(ITransport.PacketMode mode)
    {
        return mode switch
        {
            ITransport.PacketMode.RELIABLE => ENetChannel.RELIABLE,
            ITransport.PacketMode.UNRELIABLE_SEQUENCED => ENetChannel.UNRELIABLE_SEQUENCED,
            _ => ENetChannel.UNRELIABLE_UNSEQUENCED,
        };
    }
}