using ENet;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Peers;
using Host = ENet.Host;

namespace Pulse.Transport;

public sealed class ENetHostedService(
    IOptions<ENetTransportOptions> options,
    ILogger<ENetHostedService> logger,
    MessagePipe messagePipe
) : BackgroundService,
    // TODO: we could add a new class that implements this transport, but currently it is enough to keep it as the BackgroundService
    ITransport
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
        Thread.CurrentThread.Name ??= "ENet";

        using var host = new Host();

        var address = new Address();
        address.SetIP("::");
        address.Port = options.Port;

        host.Create(address, options.MaxPeers, channelLimit: ENetChannel.COUNT);

        logger.LogInformation("ENet host listening on 0.0.0.0:{Port} (maxPeers={MaxPeers}).",
            options.Port, options.MaxPeers);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Service does socket I/O + returns one event. Short timeout so we never block outgoing flushes.
            if (host.Service(1, out Event netEvent) > 0)
                HandleEvent(ref netEvent);

            // Service only returns one event per call. If multiple packets arrived in that I/O pass,
            // the rest are queued internally. CheckEvents drains them without redundant socket I/O.
            while (host.CheckEvents(out netEvent) > 0)
                HandleEvent(ref netEvent);

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
                                      PacketMode.RELIABLE => ENetChannel.RELIABLE,
                                      PacketMode.UNRELIABLE_SEQUENCED => ENetChannel.UNRELIABLE_SEQUENCED,
                                      _ => ENetChannel.UNRELIABLE_UNSEQUENCED,
                                  };

            SendToPeer(peer, channel, msg.Message);
        }
    }

    private void SendToPeer(Peer peer, ENetChannel channel, IMessage message)
    {
        int size = message.CalculateSize();
        var span = new Span<byte>(sendBuffer, 0, size);
        message.WriteTo(span);
        var packet = default(Packet);
        packet.Create(span, channel.PacketMode);
        int result = peer.Send(channel.ChannelId, ref packet);

        if (result < 0)
            PulseMetrics.Transport.SEND_FAILURES.Add(1);

        PulseMetrics.Transport.PACKETS_SENT.Add(1);
        PulseMetrics.Transport.BYTES_SENT.Add(size);
    }

    private void HandleEvent(ref Event netEvent)
    {
        var peerIndex = new PeerIndex(netEvent.Peer.ID);

        switch (netEvent.Type)
        {
            case EventType.Connect:
                netEvent.Peer.Timeout(0, options.PeerTimeoutMs, options.PeerTimeoutMs);
                connectedPeers[peerIndex] = netEvent.Peer;

                messagePipe.OnPeerConnected(peerIndex);

                PulseMetrics.Transport.PEERS_CONNECTED.Add(1);
                PulseMetrics.Transport.ACTIVE_PEERS.Add(1);

                logger.LogDebug("Peer connected: {IP}:{Port} (id={ID}).",
                    netEvent.Peer.IP, netEvent.Peer.Port, netEvent.Peer.ID);

                break;

            case EventType.Disconnect:
                connectedPeers.Remove(peerIndex);
                messagePipe.OnPeerDisconnected(peerIndex);

                PulseMetrics.Transport.PEERS_DISCONNECTED.Add(1);
                PulseMetrics.Transport.ACTIVE_PEERS.Add(-1);

                logger.LogDebug("Peer disconnected: id={ID} data={Data}.",
                    netEvent.Peer.ID, netEvent.Data);

                break;

            case EventType.Timeout:
                connectedPeers.Remove(peerIndex);
                messagePipe.OnPeerDisconnected(peerIndex);

                PulseMetrics.Transport.PEERS_DISCONNECTED.Add(1);
                PulseMetrics.Transport.ACTIVE_PEERS.Add(-1);

                logger.LogDebug("Peer timed out: id={ID}.", netEvent.Peer.ID);
                break;

            case EventType.Receive:
            {
                using Packet _ = netEvent.Packet;
                int packetLength = netEvent.Packet.Length;
                netEvent.Packet.CopyTo(receiveBuffer);

                PulseMetrics.Transport.PACKETS_RECEIVED.Add(1);
                PulseMetrics.Transport.BYTES_RECEIVED.Add(packetLength);

                messagePipe.OnDataReceived(new MessagePacket(new ReadOnlySpan<byte>(receiveBuffer, 0, packetLength), peerIndex));

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

    public void Disconnect(PeerIndex pi, DisconnectReason reason)
    {
        if (!connectedPeers.TryGetValue(pi, out Peer peer)) return;

        peer.Disconnect((uint) reason);
    }
}
