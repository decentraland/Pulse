using ENet;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport.Hardening;
using Host = ENet.Host;

namespace Pulse.Transport;

public sealed partial class ENetHostedService(
    IOptions<ENetTransportOptions> options,
    ILogger<ENetHostedService> logger,
    MessagePipe messagePipe,
    IPeerIndexAllocator peerIndexAllocator,
    IdentityBoard identityBoard,
    PreAuthAdmission preAuthAdmission
) : BackgroundService,
    // TODO: we could add a new class that implements this transport, but currently it is enough to keep it as the BackgroundService
    ITransport
{
    private readonly ENetTransportOptions options = options.Value;

    // Keyed by the server-allocated PeerIndex. Maintained exclusively on the ENet thread —
    // no locking needed.
    private readonly Dictionary<PeerIndex, Peer> connectedPeers = new ();

    // ENet peer slot id (netEvent.Peer.ID) → logical PeerIndex. ENet recycles slot ids the
    // moment a peer is freed; the allocator holds logical indexes through a grace period, so
    // the two lifecycles diverge and we translate here on every ENet event.
    private readonly Dictionary<uint, PeerIndex> slotToPeerIndex = new ();

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

        int concurrentCap = options.EffectiveMaxConcurrentConnections;
        host.Create(address, concurrentCap, channelLimit: ENetChannel.COUNT);

        logger.LogInformation(
            "ENet host listening on 0.0.0.0:{Port} (concurrentCap={Cap}, peerIndexPool={Pool}).",
            options.Port, concurrentCap, options.MaxPeers);

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
    ///     ENet is not thread-safe so we are obliged to write from the same thread we read.
    ///     Drains both data sends and disconnect requests in enqueue order — any queued
    ///     send for a peer reaches ENet before that peer's disconnect.
    /// </summary>
    private void FlushOutgoing()
    {
        while (messagePipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg))
        {
            if (!connectedPeers.TryGetValue(msg.To, out Peer peer))
                continue;

            if (msg.IsDisconnect)
            {
                peer.Disconnect((uint)msg.Disconnect!.Value);
                continue;
            }

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
        uint slotId = netEvent.Peer.ID;

        switch (netEvent.Type)
        {
            case EventType.Connect:
            {
                if (!peerIndexAllocator.TryAllocate(out PeerIndex peerIndex))
                {
                    // Pool exhausted — refuse the connection. This can happen if the pool is the
                    // same size as ENet's max peers and every pending-recycle slot is still in grace.
                    // Operator should raise the pool size or shorten the grace window.
                    logger.LogWarning("PeerIndex pool exhausted — refusing connection from {IP}:{Port}",
                        netEvent.Peer.IP, netEvent.Peer.Port);
                    netEvent.Peer.DisconnectNow((uint)DisconnectReason.SERVER_FULL);
                    break;
                }

                if (!TryAdmitOrRefuse(ref netEvent, peerIndex))
                    break;

                netEvent.Peer.Timeout(0, options.PeerTimeoutMs, options.PeerTimeoutMs);
                slotToPeerIndex[slotId] = peerIndex;
                connectedPeers[peerIndex] = netEvent.Peer;

                messagePipe.OnPeerConnected(peerIndex);

                PulseMetrics.Transport.PEERS_CONNECTED.Add(1);
                PulseMetrics.Transport.ACTIVE_PEERS.Add(1);

                logger.LogDebug("Peer connected: {IP}:{Port} (slot={Slot}, peerIndex={PeerIndex}).",
                    netEvent.Peer.IP, netEvent.Peer.Port, slotId, peerIndex);

                break;
            }

            case EventType.Disconnect:
            case EventType.Timeout:
            {
                if (!slotToPeerIndex.Remove(slotId, out PeerIndex peerIndex))
                {
                    logger.LogWarning("Unknown ENet slot {Slot} on {EventType} — nothing to release.", slotId, netEvent.Type);
                    break;
                }

                connectedPeers.Remove(peerIndex);

                // Park the slot. The allocator won't reissue it until CleanupDisconnectedPeer
                // releases it — that's the one place the simulation wipes every per-peer board,
                // so allocation and cleanup stay in lockstep.
                // PreAuthAdmission release is driven by the worker on the lifecycle Disconnected
                // event, not here — keeping all admission accounting on one thread.
                peerIndexAllocator.MarkPending(peerIndex);

                string? walletId = identityBoard.GetWalletIdByPeerIndex(peerIndex);
                messagePipe.OnPeerDisconnected(peerIndex);

                PulseMetrics.Transport.PEERS_DISCONNECTED.Add(1);
                PulseMetrics.Transport.ACTIVE_PEERS.Add(-1);

                logger.LogDebug("Peer {EventType}: slot={Slot} peerIndex={PeerIndex} wallet={Wallet} data={Data}.",
                    netEvent.Type, slotId, peerIndex, walletId ?? "<none>", netEvent.Data);
                break;
            }

            case EventType.Receive:
            {
                using Packet _ = netEvent.Packet;

                if (!slotToPeerIndex.TryGetValue(slotId, out PeerIndex peerIndex))
                {
                    // Should not happen — ENet delivers Connect before Receive for a given slot.
                    logger.LogWarning("Receive from unknown ENet slot {Slot} — dropping packet.", slotId);
                    break;
                }

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

    /// <summary>
    ///     Safe to call from any thread. The actual <c>enet_peer_disconnect</c> runs on the
    ///     ENet thread via <see cref="FlushOutgoing" /> — the native ENet API is not
    ///     thread-safe (see <c>CLAUDE.md</c>), and <see cref="connectedPeers" /> is only mutated
    ///     by the ENet thread, so both concerns are resolved by routing through
    ///     <see cref="MessagePipe" />.
    /// </summary>
    public void Disconnect(PeerIndex pi, DisconnectReason reason) =>
        messagePipe.SendDisconnect(pi, reason);
}
