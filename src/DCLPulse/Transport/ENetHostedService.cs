using ENet;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport.Geo;
using Pulse.Transport.Hardening;
using System.Diagnostics;
using Host = ENet.Host;

namespace Pulse.Transport;

public sealed partial class ENetHostedService(
    IOptions<ENetTransportOptions> options,
    ILogger<ENetHostedService> logger,
    MessagePipe messagePipe,
    IPeerIndexAllocator peerIndexAllocator,
    IdentityBoard identityBoard,
    PreAuthAdmission preAuthAdmission,
    CorruptedPacketLimiter corruptedPacketLimiter,
    ContinentResolver continentResolver
) : BackgroundService,
    // TODO: we could add a new class that implements this transport, but currently it is enough to keep it as the BackgroundService
    ITransport
{
    private const int RTT_SAMPLE_INTERVAL_MS = 5000;

    private readonly ENetTransportOptions options = options.Value;

    // Keyed by the server-allocated PeerIndex; each entry pairs the ENet peer handle with the
    // continent resolved from its IP at connect, so both share one lifecycle. Maintained
    // exclusively on the ENet thread — no locking needed.
    private readonly record struct ConnectedPeer(Peer Peer, Continent Continent);
    private readonly Dictionary<PeerIndex, ConnectedPeer> connectedPeers = new ();

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

        long lastRttSampleTimestamp = Stopwatch.GetTimestamp();

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

            if (Stopwatch.GetElapsedTime(lastRttSampleTimestamp).TotalMilliseconds >= RTT_SAMPLE_INTERVAL_MS)
            {
                SamplePeerRtt();
                lastRttSampleTimestamp = Stopwatch.GetTimestamp();
            }
        }

        ShutdownGracefully();
    }

    private void ShutdownGracefully()
    {
        // Shutdown-only allocation: flatten the peer handles out of the connected-peer map.
        var peers = new List<Peer>(connectedPeers.Count);

        foreach (ConnectedPeer cp in connectedPeers.Values)
            peers.Add(cp.Peer);

        ShutdownGracefully(peers, logger);
    }

    /// <summary>
    ///     Notify every peer in the collection with <see cref="DisconnectReason.GRACEFUL" /> and
    ///     return. Fire-and-forget: <see cref="Peer.DisconnectNow" /> emits the disconnect on the
    ///     spot and resets the peer locally, so no further ENet servicing is required. Any peer
    ///     that doesn't receive the single UDP notification falls back to its own ENet timeout —
    ///     same outcome as a hard kill, which is the worst case we have to tolerate anyway.
    ///     <para>
    ///         Internal so the test project can drive it directly against a real ENet peer pair
    ///         without constructing the whole hosted service.
    ///     </para>
    /// </summary>
    internal static void ShutdownGracefully(IReadOnlyCollection<Peer> peers, ILogger logger)
    {
        if (peers.Count == 0)
            return;

        logger.LogInformation("Graceful shutdown: notifying {Count} peer(s) with GRACEFUL.", peers.Count);

        foreach (Peer peer in peers)
            peer.DisconnectNow((uint)DisconnectReason.GRACEFUL);
    }

    /// <summary>
    ///     Runs the per-peer state teardown the Disconnect/Timeout handler does. Returns
    ///     <c>true</c> if a known peer was released. Shared with the hardening path that
    ///     uses <c>DisconnectNow</c>, which doesn't fire a local Disconnect event — so the
    ///     same bookkeeping has to run inline. Per-slot limiter state is always released
    ///     regardless of whether the slot ever bound to a peerIndex.
    /// </summary>
    private bool TeardownPeerSlot(uint slotId, string contextForLog)
    {
        corruptedPacketLimiter.ReleaseSlot(slotId);

        if (!slotToPeerIndex.Remove(slotId, out PeerIndex peerIndex))
            return false;

        connectedPeers.Remove(peerIndex);

        // Park the slot. The allocator won't reissue it until CleanupDisconnectedPeer
        // releases it — that's the one place the simulation wipes every per-peer board,
        // so allocation and cleanup stay in lockstep.
        // PreAuthAdmission release is driven by the worker on the lifecycle Disconnected
        // event, not here — keeping all admission accounting on one thread.
        peerIndexAllocator.MarkPending(peerIndex);
        corruptedPacketLimiter.Release(peerIndex);

        string? walletId = identityBoard.GetWalletIdByPeerIndex(peerIndex);
        messagePipe.OnPeerDisconnected(peerIndex);

        PulseMetrics.Transport.PEERS_DISCONNECTED.Add(1);
        PulseMetrics.Transport.ACTIVE_PEERS.Add(-1);

        logger.LogDebug("Peer teardown ({Context}): slot={Slot} peerIndex={PeerIndex} wallet={Wallet}.",
            contextForLog, slotId, peerIndex, walletId ?? "<none>");
        return true;
    }

    /// <summary>
    ///     ENet is not thread-safe so we are obliged to write from the same thread we read.
    ///     Drains both data sends and disconnect requests in enqueue order — any queued
    ///     send for a peer reaches ENet before that peer's disconnect.
    /// </summary>
    private void FlushOutgoing()
    {
        long drainStart = Stopwatch.GetTimestamp();
        var drained = 0;

        while (messagePipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg))
        {
            drained++;

            if (!connectedPeers.TryGetValue(msg.To, out ConnectedPeer cp))
                continue;

            if (msg.IsDisconnect)
            {
                cp.Peer.Disconnect((uint)msg.Disconnect!.Value);
                continue;
            }

            ENetChannel channel = msg.PacketMode switch
                                  {
                                      PacketMode.RELIABLE => ENetChannel.RELIABLE,
                                      PacketMode.UNRELIABLE_SEQUENCED => ENetChannel.UNRELIABLE_SEQUENCED,
                                      _ => ENetChannel.UNRELIABLE_UNSEQUENCED,
                                  };

            SendToPeer(cp.Peer, channel, msg.Message);
        }

        RecordDrainCycle(drainStart, drained);
    }

    /// <summary>
    ///     M3 measurement: outbound drain-cycle duration (µs). Bounds the queue wait of any
    ///     outgoing message without per-message timestamps. Empty cycles are not recorded —
    ///     they would flood the histogram with zeros at the ENet loop frequency.
    /// </summary>
    internal static void RecordDrainCycle(long startTimestamp, int drained)
    {
        if (drained == 0)
            return;

        PulseMetrics.Transport.OUTGOING_DRAIN_CYCLE_US.Record(
            (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds);
    }

    /// <summary>
    ///     M4 sampling sweep: record every connected peer's smoothed RTT into its continent
    ///     histogram. Runs on the ENet thread every <see cref="RTT_SAMPLE_INTERVAL_MS" />.
    ///     ENet seeds RoundTripTime at 500 ms until the first reliable-channel ACK sample
    ///     arrives, so a peer's first sweep entry may carry that seed — accepted noise at
    ///     a 5 s cadence rather than a reason to track per-peer connect ages.
    /// </summary>
    private void SamplePeerRtt()
    {
        foreach (ConnectedPeer cp in connectedPeers.Values)
            RecordPeerRtt(cp.Continent, cp.Peer.RoundTripTime);
    }

    /// <summary>
    ///     Records one RTT sample on the continent's instrument. Internal static so the
    ///     routing (including the out-of-range clamp) is testable without a live ENet host.
    /// </summary>
    internal static void RecordPeerRtt(Continent continent, uint rttMs)
    {
        int index = Math.Min((int)continent, PulseMetrics.Transport.PEER_RTT_MS.Length - 1);
        PulseMetrics.Transport.PEER_RTT_MS[index].Record(rttMs);
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
                connectedPeers[peerIndex] = new ConnectedPeer(netEvent.Peer, continentResolver.Resolve(netEvent.Peer.IP));

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
                if (!TeardownPeerSlot(slotId, netEvent.Type.ToString()))
                    logger.LogWarning("Unknown ENet slot {Slot} on {EventType} — nothing to release.", slotId, netEvent.Type);

                break;
            }

            case EventType.Receive:
            {
                using Packet _ = netEvent.Packet;

                if (!slotToPeerIndex.TryGetValue(slotId, out PeerIndex peerIndex))
                {
                    // Should not happen — ENet delivers Connect before Receive for a given slot.
                    // Treat as a corrupted packet so a peer that floods this race window still
                    // hits the same per-slot budget.
                    logger.LogWarning("Receive from unknown ENet slot {Slot} — counting against corruption budget.", slotId);
                    RecordCorruptionForSlot(ref netEvent, slotId);
                    break;
                }

                int packetLength = netEvent.Packet.Length;

                if (CheckOversized(ref netEvent, peerIndex, packetLength))
                    break;

                netEvent.Packet.CopyTo(receiveBuffer);

                PulseMetrics.Transport.PACKETS_RECEIVED.Add(1);
                PulseMetrics.Transport.BYTES_RECEIVED.Add(packetLength);

                if (!messagePipe.OnDataReceived(new MessagePacket(new ReadOnlySpan<byte>(receiveBuffer, 0, packetLength), peerIndex)))
                    RecordCorruption(ref netEvent, peerIndex);

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
