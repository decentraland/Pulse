using ENet;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Pulse.Messaging;
using Pulse.Peers;
using Host = ENet.Host;

namespace Pulse.Transport;

public sealed class ENetHostedService(
    IOptions<ENetTransportOptions> options,
    ILogger<ENetHostedService> logger,
    MessagePipe messagePipe
) : BackgroundService
{
    private readonly ENetTransportOptions options = options.Value;

    // Keyed by ENet peer ID. Maintained exclusively on the ENet thread — no locking needed.
    private readonly Dictionary<PeerIndex, Peer> connectedPeers = new ();
    private readonly byte[] receiveBuffer = new byte[options.Value.BufferSize];
    private readonly byte[] sendBuffer = new byte[options.Value.BufferSize];

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Library.Initialize())
            throw new InvalidOperationException("ENet library failed to initialize.");

        logger.LogInformation("ENet initialized (version {Version}).", Library.version);
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ENet must be driven on a single dedicated thread — LongRunning prevents
        // the thread-pool from treating this as a short-lived async continuation.
        return Task.Factory.StartNew(
            () => RunLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        using var host = new Host();

        var address = new Address();
        address.SetIP("::");
        address.Port = options.Port;

        host.Create(address, options.MaxPeers, channelLimit: ENetChannel.COUNT);

        logger.LogInformation("ENet host listening on 0.0.0.0:{Port} (maxPeers={MaxPeers}).",
            options.Port, options.MaxPeers);

        while (!stoppingToken.IsCancellationRequested)
        {
            var polled = false;

            while (!polled)
            {
                if (host.CheckEvents(out Event netEvent) <= 0)
                {
                    if (host.Service(options.ServiceTimeoutMs, out netEvent) <= 0)
                        break;

                    polled = true;
                }

                HandleEvent(ref netEvent);
            }

            FlushOutgoing();
        }

        host.Flush();
    }

    /// <summary>
    ///     ENet is not thread-safe so we are obliged to write from the same thread we read
    /// </summary>
    private void FlushOutgoing()
    {
        while (messagePipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg))
        {
            if (!connectedPeers.TryGetValue(msg.To, out Peer peer))
                continue;

            ENetChannel channel = msg.PacketMode switch
                                  {
                                      ITransport.PacketMode.RELIABLE => ENetChannel.RELIABLE,
                                      ITransport.PacketMode.UNRELIABLE_SEQUENCED => ENetChannel.UNRELIABLE_SEQUENCED,
                                      _ => ENetChannel.UNRELIABLE_UNSEQUENCED,
                                  };

            SendToPeer(peer, channel, msg.Message);
        }
    }

    private void SendToPeer(Peer peer, ENetChannel channel, IMessage message)
    {
        int size = message.CalculateSize();
        message.WriteTo(new Span<byte>(sendBuffer, 0, size));
        var packet = default(Packet);
        packet.Create(sendBuffer, 0, size, channel.PacketMode);
        peer.Send(channel.ChannelId, ref packet);
    }

    private void HandleEvent(ref Event netEvent)
    {
        var peerIndex = new PeerIndex(netEvent.Peer.ID);

        switch (netEvent.Type)
        {
            case EventType.Connect:
                connectedPeers[peerIndex] = netEvent.Peer;

                logger.LogDebug("Peer connected: {IP}:{Port} (id={ID}).",
                    netEvent.Peer.IP, netEvent.Peer.Port, netEvent.Peer.ID);

                break;

            case EventType.Disconnect:
                connectedPeers.Remove(peerIndex);

                logger.LogDebug("Peer disconnected: id={ID} data={Data}.",
                    netEvent.Peer.ID, netEvent.Data);

                break;

            case EventType.Timeout:
                connectedPeers.Remove(peerIndex);
                logger.LogDebug("Peer timed out: id={ID}.", netEvent.Peer.ID);
                break;

            case EventType.Receive:
            {
                using Packet _ = netEvent.Packet;
                netEvent.Packet.CopyTo(receiveBuffer);

                messagePipe.OnDataReceived(new MessagePacket(new ReadOnlySpan<byte>(receiveBuffer, 0, netEvent.Packet.Length), peerIndex));

                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Library.Deinitialize();
        logger.LogInformation("ENet deinitialized.");
    }
}
