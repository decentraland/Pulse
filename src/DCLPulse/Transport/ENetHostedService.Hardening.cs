using ENet;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport.Hardening;

namespace Pulse.Transport;

/// <summary>
///     Hardening hooks for <see cref="ENetHostedService" /> — kept in a partial file so the
///     protection logic lives apart from the transport's core event loop.
/// </summary>
public sealed partial class ENetHostedService
{
    // How often at most a per-IP refusal is reported. Touched only from the ENet thread.
    private const int IP_LIMIT_LOG_INTERVAL_MS = 10_000;

    private readonly RefusalLogThrottle ipLimitRefusalLog = new (IP_LIMIT_LOG_INTERVAL_MS);

    /// <summary>
    ///     Whole admission sequence for an inbound ENet connection, cheapest gate first. The
    ///     per-source-IP cap runs before <c>TryAllocate</c>, so a refused connection draws no
    ///     <see cref="PeerIndex" />, leaves the allocator's pending-recycle window untouched and
    ///     never reaches <c>OnPeerConnected</c>. Owns every rollback — only an admitted peer is
    ///     released from a worker, so a refusal after the slot was reserved hands it back with
    ///     <see cref="IpLimiter.Abandon" />; <see cref="IpLimiter.Bind" /> commits it on the way out.
    ///     The address is read once and threaded through, so no gate sees a different string than
    ///     <see cref="IpLimiter.TryAcquire" /> counted.
    /// </summary>
    /// <returns>
    ///     <c>true</c> when admitted, <paramref name="peerIndex" /> holding the allocated index;
    ///     <c>false</c> when refused and disconnected.
    /// </returns>
    private bool TryAdmitConnection(ref Event netEvent, out PeerIndex peerIndex)
    {
        peerIndex = default;
        string peerIp = netEvent.Peer.IP;

        if (!ipLimiter.TryAcquire(peerIp))
        {
            LogIpLimitRefusal(peerIp, netEvent.Peer.Port);
            netEvent.Peer.DisconnectNow((uint)DisconnectReason.IP_CONNECTION_LIMIT_EXCEEDED);
            return false;
        }

        // Allocate a slot stamped as ENet-owned; the stamp rides on the PeerIndex through every
        // store it lands in (slotToPeerIndex, connectedPeers, the worker's peerStates, IdentityBoard).
        if (!peerIndexAllocator.TryAllocate(TransportId.ENet, out peerIndex))
        {
            // Pool exhausted — refuse the connection. This can happen if the pool is the
            // same size as ENet's max peers and every pending-recycle slot is still in grace.
            // Operator should raise the pool size or shorten the grace window.
            ipLimiter.Abandon(peerIp);

            logger.LogWarning("PeerIndex pool exhausted — refusing connection from {IP}:{Port}",
                peerIp, netEvent.Peer.Port);

            netEvent.Peer.DisconnectNow((uint)DisconnectReason.SERVER_FULL);
            return false;
        }

        if (!TryAdmitOrRefuse(ref netEvent, peerIndex, peerIp))
            return false;

        // Commit: keyed by PeerIndex from here, released by the worker on Disconnected.
        ipLimiter.Bind(peerIndex, peerIp);
        return true;
    }

    /// <summary>
    ///     Reports a per-IP refusal at a bounded rate — the refusal is the cheapest gate on the
    ///     connect path and its rate is attacker-controlled. <c>ip_limit_refused</c> carries the
    ///     volume, this carries the address. See <see cref="RefusalLogThrottle" />.
    /// </summary>
    private void LogIpLimitRefusal(string peerIp, ushort port)
    {
        if (!ipLimitRefusalLog.ShouldEmit(out long suppressed))
            return;

        logger.LogWarning(
            "Per-IP connection limit exceeded — refusing connection from {IP}:{Port} ({Suppressed} further refusal(s) suppressed since the previous message; see the ip_limit_refused counter for the full rate).",
            peerIp, port, suppressed);
    }

    /// <summary>
    ///     Runs pre-auth admission control on a freshly-allocated peer. On refusal, rolls back both
    ///     reservations taken for this connection — the PeerIndex pool allocation and the
    ///     per-source-IP slot — and disconnects with the specific reason so the client can
    ///     distinguish retryable transients from terminal failures.
    /// </summary>
    /// <returns><c>true</c> if the peer is admitted; <c>false</c> if refused and disconnected.</returns>
    private bool TryAdmitOrRefuse(ref Event netEvent, PeerIndex peerIndex, string peerIp)
    {
        PreAuthAdmission.AdmitResult result = preAuthAdmission.TryAdmit(peerIndex, peerIp);

        if (result == PreAuthAdmission.AdmitResult.OK)
            return true;

        // Rollback pool allocation — slot returns to the free list for the next connect.
        peerIndexAllocator.MarkPending(peerIndex);
        peerIndexAllocator.Release(peerIndex);

        // No lifecycle event ever fires for a peer refused here, so the IP slot goes back inline.
        ipLimiter.Abandon(peerIp);

        DisconnectReason reason = result == PreAuthAdmission.AdmitResult.IP_LIMIT_EXHAUSTED
            ? DisconnectReason.PRE_AUTH_IP_LIMIT_EXHAUSTED
            : DisconnectReason.PRE_AUTH_BUDGET_EXHAUSTED;

        logger.LogWarning("Pre-auth admission refused ({Reason}) for {IP}:{Port}",
            reason, peerIp, netEvent.Peer.Port);

        netEvent.Peer.DisconnectNow((uint)reason);
        return false;
    }

    /// <summary>
    ///     Drops the Receive event when the inbound packet wouldn't fit in <see cref="receiveBuffer" />.
    ///     Two thresholds:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <c>length &gt; BufferSize</c> but <c>≤ 2× BufferSize</c>: count against the
    ///                 peer's corruption budget. A handful of these can be the symptom of a buggy
    ///                 client or a transient middlebox glitch; the bucket absorbs the burst.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>length &gt; 2× BufferSize</c>: terminal. No well-formed client produces
    ///                 packets at this size, and queued <c>Disconnect</c> propagates too slowly to
    ///                 outpace a sustained attack — we <c>DisconnectNow</c> and tear down the slot
    ///                 inline so the next packet from the peer can't reach the handler.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </summary>
    /// <returns><c>true</c> if the packet was rejected (oversized); <c>false</c> if it fits and should be processed.</returns>
    private bool CheckOversized(ref Event netEvent, PeerIndex peerIndex, int packetLength)
    {
        if (packetLength <= receiveBuffer.Length)
            return false;

        if (packetLength > receiveBuffer.Length * 2)
        {
            HardDisconnectGrosslyOversized(ref netEvent, peerIndex, packetLength);
            return true;
        }

        logger.LogWarning(
            "Oversized packet from slot {Slot} ({IP}:{Port}, peerIndex={PeerIndex}): {Length} bytes > {Cap} byte buffer — counting against corruption budget.",
            netEvent.Peer.ID, netEvent.Peer.IP, netEvent.Peer.Port, peerIndex, packetLength, receiveBuffer.Length);

        RecordCorruption(ref netEvent, peerIndex);
        return true;
    }

    /// <summary>
    ///     No-allowance path for packets larger than twice the receive buffer. Forces the peer
    ///     down immediately via <c>DisconnectNow</c> (no ENet outgoing queue, no local
    ///     Disconnect event) and runs the per-peer teardown inline so the slot, allocator,
    ///     and worker observers all see the eviction before the next ENet service tick.
    /// </summary>
    private void HardDisconnectGrosslyOversized(ref Event netEvent, PeerIndex peerIndex, int packetLength)
    {
        PulseMetrics.Hardening.CORRUPTED_PACKET.Add(1);

        logger.LogWarning(
            "Grossly oversized packet from slot {Slot} ({IP}:{Port}, peerIndex={PeerIndex}): {Length} bytes > 2× {Cap} byte buffer — hard-disconnecting with {Reason}.",
            netEvent.Peer.ID, netEvent.Peer.IP, netEvent.Peer.Port, peerIndex, packetLength, receiveBuffer.Length, DisconnectReason.PACKET_CORRUPTED);

        uint slotId = netEvent.Peer.ID;
        netEvent.Peer.DisconnectNow((uint)DisconnectReason.PACKET_CORRUPTED);
        TeardownPeerSlot(slotId, nameof(DisconnectReason.PACKET_CORRUPTED));
    }

    /// <summary>
    ///     Bumps the corruption metric and debits the peer's token-bucket budget. When the
    ///     budget is exhausted the peer is queued for disconnect with
    ///     <see cref="DisconnectReason.PACKET_CORRUPTED" />. <c>Disconnect</c> (queued, not
    ///     <c>DisconnectNow</c>) so ENet still fires a Disconnect event for the slot and the
    ///     existing cleanup path runs — including <see cref="CorruptedPacketLimiter.Release" />
    ///     in the lifecycle handler.
    /// </summary>
    private void RecordCorruption(ref Event netEvent, PeerIndex peerIndex)
    {
        PulseMetrics.Hardening.CORRUPTED_PACKET.Add(1);

        if (!corruptedPacketLimiter.RegisterAndCheckExhausted(peerIndex))
            return;

        logger.LogWarning(
            "Corrupted-packet budget exhausted for slot {Slot} ({IP}:{Port}, peerIndex={PeerIndex}) — disconnecting with {Reason}.",
            netEvent.Peer.ID, netEvent.Peer.IP, netEvent.Peer.Port, peerIndex, DisconnectReason.PACKET_CORRUPTED);

        netEvent.Peer.Disconnect((uint)DisconnectReason.PACKET_CORRUPTED);
    }

    /// <summary>
    ///     Counterpart to <see cref="RecordCorruption" /> for Receive events delivered on an
    ///     ENet slot that has no <see cref="PeerIndex" /> mapping yet — same per-slot budget,
    ///     so a flood of "phantom" packets can't escape the rate limit just by missing the
    ///     known-peer dict. On exhaust we use <c>DisconnectNow</c> because no normal
    ///     lifecycle is in progress for this slot; otherwise the bucket entry would leak.
    /// </summary>
    private void RecordCorruptionForSlot(ref Event netEvent, uint slotId)
    {
        PulseMetrics.Hardening.CORRUPTED_PACKET.Add(1);

        if (!corruptedPacketLimiter.RegisterAndCheckExhaustedForSlot(slotId))
            return;

        logger.LogWarning(
            "Corrupted-packet budget exhausted for unknown slot {Slot} ({IP}:{Port}) — disconnecting with {Reason}.",
            slotId, netEvent.Peer.IP, netEvent.Peer.Port, DisconnectReason.PACKET_CORRUPTED);

        netEvent.Peer.DisconnectNow((uint)DisconnectReason.PACKET_CORRUPTED);
        corruptedPacketLimiter.ReleaseSlot(slotId);
    }
}
