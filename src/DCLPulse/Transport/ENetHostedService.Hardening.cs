using ENet;
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
}
