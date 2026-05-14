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
    /// <summary>
    ///     Runs pre-auth admission control on a freshly-allocated peer. On refusal, rolls back
    ///     the PeerIndex pool allocation and disconnects the peer with the specific reason so
    ///     the client can distinguish retryable transients from terminal failures.
    /// </summary>
    /// <returns><c>true</c> if the peer is admitted; <c>false</c> if refused and disconnected.</returns>
    private bool TryAdmitOrRefuse(ref Event netEvent, PeerIndex peerIndex)
    {
        string peerIp = netEvent.Peer.IP;
        PreAuthAdmission.AdmitResult result = preAuthAdmission.TryAdmit(peerIndex, peerIp);

        if (result == PreAuthAdmission.AdmitResult.OK)
            return true;

        // Rollback pool allocation — slot returns to the free list for the next connect.
        peerIndexAllocator.MarkPending(peerIndex);
        peerIndexAllocator.Release(peerIndex);

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
