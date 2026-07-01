using DCL.WebTransport;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport.Hardening;
using Pulse.Transport.WebTransport;

namespace Pulse.Transport;

/// <summary>
///     WebTransport transport, the browser-facing sibling of <see cref="ENetHostedService" />. Drives
///     the native <see cref="IWebTransportHost" /> from one dedicated thread — drain an event, service
///     outgoing — and bridges it to the shared <see cref="MessagePipe" />. Peers get a WebTransport-
///     stamped <see cref="PeerIndex" /> from the shared <see cref="IPeerIndexAllocator" />, so
///     everything downstream of the transport seam (auth, workers, snapshot ring, hardening) treats
///     them identically to ENet peers. Channel semantics map <see cref="PacketMode" /> onto QUIC:
///     reliable → the peer's bidi stream (length-framed), unreliable → datagrams. Inbound sequenced
///     datagrams (client→server) carry a {channelId, seq} header and are stale-dropped to replicate
///     ENet's behaviour; outbound datagrams (server→client) are sent raw, because every unreliable
///     server→client message (STATE_DELTA) already carries its own sequence in the body.
/// </summary>
public sealed class WebTransportHostedService(
    IWebTransportHost host,
    IOptions<WebTransportOptions> options,
    ILogger<WebTransportHostedService> logger,
    MessagePipe messagePipe,
    IPeerIndexAllocator peerIndexAllocator,
    IdentityBoard identityBoard,
    PreAuthAdmission preAuthAdmission,
    CorruptedPacketLimiter corruptedPacketLimiter
) : BackgroundService
{
    // Inbound datagrams (client→server) carry a channel id in their header. Channel 1 is stale-dropped
    // to replicate ENet's unreliable-sequenced behaviour; any other channel is delivered as-is.
    // Outbound datagrams carry no header — see SendDatagram.
    private const byte CHANNEL_SEQUENCED = 1;

    // The transport dimension attached to every transport counter this service records.
    private static readonly KeyValuePair<string, object?> TRANSPORT_TAG = PulseMetrics.Transport.Tag(TransportId.WebTransport);

    private readonly WebTransportOptions options = options.Value;

    // Session state is owned exclusively by the WebTransport thread — no locking, same discipline as
    // ENet's connectedPeers. `sessions` is keyed by the native session id: the inbound path starts
    // there and needs the peer index plus the per-connection codec together, so they live in one
    // struct. `wtIdByPeerIndex` is the reverse index the outbound path uses to resolve a session from
    // an outgoing message's PeerIndex. One bidirectional map over the connect→teardown lifecycle.
    private readonly Dictionary<ulong, WtPeerSession> sessions = new ();
    private readonly Dictionary<PeerIndex, ulong> wtIdByPeerIndex = new ();

    private readonly byte[] sendBuffer = new byte[options.Value.MaxMessageBytes];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The native host is driven on a single dedicated thread — LongRunning keeps the thread-pool
        // from treating this as a short-lived async continuation (same rationale as ENet).
        return Task.Factory.StartNew(
            () => RunLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        Thread.CurrentThread.Name ??= "WebTransport";

        logger.LogInformation("WebTransport host listening on {BindAddr}.", options.BindAddr);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (host.TryService(options.ServiceTimeoutMs, out WebTransportEvent ev))
                HandleEvent(ev);

            FlushOutgoing();
        }

        ShutdownGracefully();
    }

    private void ShutdownGracefully()
    {
        if (sessions.Count == 0)
            return;

        logger.LogInformation("WebTransport graceful shutdown: closing {Count} peer(s).", sessions.Count);

        // No structural mutation happens here — the Disconnect events fire only on the next service
        // pass, which never runs because the loop has exited — so iterating the keys is safe.
        foreach (ulong wtId in sessions.Keys)
            host.Disconnect(wtId, (uint)DisconnectReason.GRACEFUL);
    }

    /// <summary>
    ///     Dispatches one drained host event. Internal so the test project can drive the transport's
    ///     event handling directly, without spinning up the native host and its thread.
    /// </summary>
    internal void HandleEvent(WebTransportEvent ev)
    {
        switch (ev.Kind)
        {
            case WebTransportEventKind.Connect:
                HandleConnect(ev);
                break;
            case WebTransportEventKind.StreamData:
                HandleStreamData(ev);
                break;
            case WebTransportEventKind.Datagram:
                HandleDatagram(ev);
                break;
            case WebTransportEventKind.Disconnect:
                HandleDisconnect(ev);
                break;
        }
    }

    private void HandleConnect(WebTransportEvent ev)
    {
        if (!peerIndexAllocator.TryAllocate(TransportId.WebTransport, out PeerIndex peerIndex))
        {
            // Shared pool exhausted (both transports draw from it). Refuse on the WT handle directly —
            // no PeerIndex was issued, so nothing to tear down.
            logger.LogWarning("PeerIndex pool exhausted — refusing WebTransport connection from {Address}.", ev.RemoteAddress);
            host.Disconnect(ev.PeerId, (uint)DisconnectReason.SERVER_FULL);
            return;
        }

        if (!TryAdmitOrRefuse(ev, peerIndex))
            return;

        sessions[ev.PeerId] = new WtPeerSession(peerIndex, options.MaxMessageBytes);
        wtIdByPeerIndex[peerIndex] = ev.PeerId;

        messagePipe.OnPeerConnected(peerIndex);

        PulseMetrics.Transport.PEERS_CONNECTED.Add(1, TRANSPORT_TAG);
        PulseMetrics.Transport.ACTIVE_PEERS.Add(1, TRANSPORT_TAG);

        logger.LogDebug("WebTransport peer connected: {Address} (wtId={WtId}, peerIndex={PeerIndex}).",
            ev.RemoteAddress, ev.PeerId, peerIndex);
    }

    /// <summary>
    ///     Runs pre-auth admission on a freshly-allocated peer, sharing the budget with ENet. On
    ///     refusal, rolls the PeerIndex allocation back and closes the WT session with the specific
    ///     reason. Returns <c>true</c> when admitted.
    /// </summary>
    private bool TryAdmitOrRefuse(WebTransportEvent ev, PeerIndex peerIndex)
    {
        PreAuthAdmission.AdmitResult result = preAuthAdmission.TryAdmit(peerIndex, ParseIp(ev.RemoteAddress));

        if (result == PreAuthAdmission.AdmitResult.OK)
            return true;

        peerIndexAllocator.MarkPending(peerIndex);
        peerIndexAllocator.Release(peerIndex);

        DisconnectReason reason = result == PreAuthAdmission.AdmitResult.IP_LIMIT_EXHAUSTED
            ? DisconnectReason.PRE_AUTH_IP_LIMIT_EXHAUSTED
            : DisconnectReason.PRE_AUTH_BUDGET_EXHAUSTED;

        logger.LogWarning("Pre-auth admission refused ({Reason}) for {Address}.", reason, ev.RemoteAddress);
        host.Disconnect(ev.PeerId, (uint)reason);
        return false;
    }

    private void HandleStreamData(WebTransportEvent ev)
    {
        if (!sessions.TryGetValue(ev.PeerId, out WtPeerSession session))
        {
            logger.LogWarning("StreamData from unknown WebTransport peer {WtId} — ignoring.", ev.PeerId);
            return;
        }

        try
        {
            session.StreamReader.Append(ev.Data);

            while (session.StreamReader.TryRead(out byte[] message))
                DeliverInbound(session.PeerIndex, message);
        }
        catch (InvalidDataException exception)
        {
            // A frame declared a length past the cap — treat as corruption and debit the budget.
            logger.LogWarning(exception, "Oversized stream frame from peer {PeerIndex} — counting against corruption budget.", session.PeerIndex);
            RecordCorruption(session.PeerIndex);
        }
    }

    private void HandleDatagram(WebTransportEvent ev)
    {
        if (!sessions.TryGetValue(ev.PeerId, out WtPeerSession session))
        {
            logger.LogWarning("Datagram from unknown WebTransport peer {WtId} — ignoring.", ev.PeerId);
            return;
        }

        if (!DatagramFraming.TryParse(ev.Data, out byte channelId, out uint seq, out ReadOnlySpan<byte> payload))
        {
            logger.LogWarning("Malformed datagram from peer {PeerIndex} — counting against corruption budget.", session.PeerIndex);
            RecordCorruption(session.PeerIndex);
            return;
        }

        // Replicate ENet's unreliable-sequenced drop: on the sequenced channel keep only datagrams
        // newer than the last accepted; the unsequenced channel is delivered as-is.
        if (channelId == CHANNEL_SEQUENCED && !session.Deduper.ShouldAccept(channelId, seq))
        {
            PulseMetrics.WebTransport.DATAGRAMS_DROPPED_STALE.Add(1);
            return;
        }

        DeliverInbound(session.PeerIndex, payload);
    }

    private void HandleDisconnect(WebTransportEvent ev)
    {
        // The native host emits Disconnect on any close — peer-initiated, transport error, or a
        // server-initiated host.Disconnect — so this one path tears every session down. The atomic
        // remove is the idempotency gate: a repeat Disconnect for the same id finds nothing.
        if (!sessions.Remove(ev.PeerId, out WtPeerSession session))
        {
            logger.LogWarning("Disconnect for unknown WebTransport peer {WtId} — nothing to release.", ev.PeerId);
            return;
        }

        TeardownPeer(session.PeerIndex, ev.PeerId);
    }

    private void TeardownPeer(PeerIndex peerIndex, ulong wtId)
    {
        wtIdByPeerIndex.Remove(peerIndex);
        corruptedPacketLimiter.Release(peerIndex);

        // Park the slot. The allocator reissues it only after CleanupDisconnectedPeer wipes every
        // per-peer board, keeping allocation and cleanup in lockstep. PreAuthAdmission release is
        // driven by the worker on the lifecycle Disconnected event, not here.
        peerIndexAllocator.MarkPending(peerIndex);

        string? walletId = identityBoard.GetWalletIdByPeerIndex(peerIndex);
        messagePipe.OnPeerDisconnected(peerIndex);

        PulseMetrics.Transport.PEERS_DISCONNECTED.Add(1, TRANSPORT_TAG);
        PulseMetrics.Transport.ACTIVE_PEERS.Add(-1, TRANSPORT_TAG);

        logger.LogDebug("WebTransport peer teardown: wtId={WtId} peerIndex={PeerIndex} wallet={Wallet}.",
            wtId, peerIndex, walletId ?? "<none>");
    }

    private void DeliverInbound(PeerIndex peerIndex, ReadOnlySpan<byte> message)
    {
        PulseMetrics.Transport.PACKETS_RECEIVED.Add(1, TRANSPORT_TAG);
        PulseMetrics.Transport.BYTES_RECEIVED.Add(message.Length, TRANSPORT_TAG);

        if (!messagePipe.OnDataReceived(new MessagePacket(message, peerIndex)))
            RecordCorruption(peerIndex);
    }

    private void RecordCorruption(PeerIndex peerIndex)
    {
        PulseMetrics.Hardening.CORRUPTED_PACKET.Add(1);

        if (!corruptedPacketLimiter.RegisterAndCheckExhausted(peerIndex))
            return;

        // Close the session; the resulting Disconnect event runs the normal teardown + worker cleanup.
        if (wtIdByPeerIndex.TryGetValue(peerIndex, out ulong wtId))
        {
            logger.LogWarning("Corrupted-packet budget exhausted for peer {PeerIndex} — disconnecting with {Reason}.",
                peerIndex, DisconnectReason.PACKET_CORRUPTED);
            host.Disconnect(wtId, (uint)DisconnectReason.PACKET_CORRUPTED);
        }
    }

    /// <summary>
    ///     Drains this transport's outgoing channel and maps each <see cref="PacketMode" /> onto a
    ///     QUIC primitive. Internal so tests can drive the outgoing path deterministically.
    /// </summary>
    internal void FlushOutgoing()
    {
        while (messagePipe.TryReadOutgoingMessage(TransportId.WebTransport, out MessagePipe.OutgoingMessage msg))
        {
            if (!wtIdByPeerIndex.TryGetValue(msg.To, out ulong wtId))
                continue;

            if (msg.IsDisconnect)
            {
                host.Disconnect(wtId, (uint)msg.Disconnect!.Value);
                continue;
            }

            SendToPeer(msg.To, wtId, msg.PacketMode, msg.Message);
        }
    }

    private void SendToPeer(PeerIndex peerIndex, ulong wtId, PacketMode packetMode, IMessage message)
    {
        int size = message.CalculateSize();

        if (size > sendBuffer.Length)
        {
            logger.LogError("Outgoing WebTransport message ({Size} B) exceeds the {Cap} B buffer for peer {PeerIndex} — dropping.",
                size, sendBuffer.Length, peerIndex);
            PulseMetrics.Transport.SEND_FAILURES.Add(1, TRANSPORT_TAG);
            return;
        }

        var payload = new Span<byte>(sendBuffer, 0, size);
        message.WriteTo(payload);

        bool sent = packetMode == PacketMode.RELIABLE
                        ? host.SendStream(wtId, StreamFraming.Frame(payload))
                        : SendDatagram(peerIndex, wtId, payload);

        if (!sent)
            PulseMetrics.Transport.SEND_FAILURES.Add(1, TRANSPORT_TAG);

        PulseMetrics.Transport.PACKETS_SENT.Add(1, TRANSPORT_TAG);
        PulseMetrics.Transport.BYTES_SENT.Add(size, TRANSPORT_TAG);
    }

    private bool SendDatagram(PeerIndex peerIndex, ulong wtId, ReadOnlySpan<byte> payload)
    {
        // A raw datagram — one QUIC datagram carries one message (datagrams are already bounded, unlike
        // streams), and every unreliable server→client message (STATE_DELTA) carries its own sequence
        // in the body, so no transport-level sequence header is added; the client detects staleness from
        // that body sequence. Only the client→server direction, whose payload has no intrinsic sequence,
        // frames a {channelId, seq} header for the inbound deduper.
        if (payload.Length > options.MaxDatagramBytes)
        {
            // A message on the unreliable channel that doesn't fit a datagram is a server bug — it
            // outgrew the path-MTU budget. Drop it and raise an error for a developer to fix; do not
            // reroute it to the reliable stream, which would mask the regression and change the
            // channel's semantics (unreliable → head-of-line-blocking reliable).
            logger.LogError(
                "Unreliable message ({Size} B) exceeds the {Cap} B datagram cap for peer {PeerIndex} and was dropped — a message on the unreliable channel must fit the datagram budget; investigate the oversized message.",
                payload.Length, options.MaxDatagramBytes, peerIndex);
            PulseMetrics.WebTransport.DATAGRAMS_DROPPED_OVERSIZE.Add(1);
            return false;
        }

        return host.SendDatagram(wtId, payload.ToArray());
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Dispose the shared host to close the QUIC endpoint promptly on graceful stop. It is a DI
        // singleton, so the container disposes it again at teardown — the native Dispose is idempotent,
        // so the second call is a no-op.
        host.Dispose();
        logger.LogInformation("WebTransport host stopped.");
    }

    private static string ParseIp(string remoteAddress) =>
        // Key admission by source IP: each connection has a distinct ephemeral port, so the port must
        // be stripped or the per-IP cap would never bind. Falls back to the raw string if unparsable.
        System.Net.IPEndPoint.TryParse(remoteAddress, out System.Net.IPEndPoint? endpoint)
            ? endpoint.Address.ToString()
            : remoteAddress;

    /// <summary>
    ///     Per-session state keyed by the native session id: the peer's logical index plus its inbound
    ///     channel-semantics state (stream reassembly + datagram staleness-drop). No outbound sequencer
    ///     — server→client datagrams are sent raw (see <see cref="SendDatagram" />). The reference-typed
    ///     members are shared, so the struct is safe to copy out of the map and drive in place.
    /// </summary>
    private readonly struct WtPeerSession(PeerIndex peerIndex, int maxMessageBytes)
    {
        public PeerIndex PeerIndex { get; } = peerIndex;

        public StreamFrameReader StreamReader { get; } = new (maxMessageBytes);

        public DatagramDeduper Deduper { get; } = new ();
    }
}
