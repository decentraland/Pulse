using Pulse.Peers;
using Pulse.Transport;
using System.Diagnostics.Metrics;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Base class for post-auth per-message defenses that reject a peer on violation. The
///     shared shape is: on failure, increment a counter and disconnect the peer with a
///     specific reason. Derived classes pick the counter at construction and pass a reason
///     per-call via <see cref="Reject" />.
///     <para />
///     Not used by <c>PreAuthAdmission</c> — that defense returns an enum, runs on the ENet
///     thread, and uses <c>peer.DisconnectNow</c> rather than the worker-thread <c>Disconnect</c>
///     route, so the abstraction doesn't fit.
/// </summary>
public abstract class PeerDefense(ITransport transport, Counter<long> violationMetric)
{
    /// <summary>
    ///     Records a violation and disconnects <paramref name="from" />. Flips the peer into
    ///     <see cref="PeerConnectionState.PENDING_DISCONNECT" /> synchronously so subsequent
    ///     messages queued from the same peer fail <c>SkipFromUnauthorizedPeer</c> before
    ///     ENet's Disconnect event finalises the transition to <c>DISCONNECTING</c>. Always
    ///     returns <c>false</c> so callers can <c>return Reject(...)</c> in a single line.
    /// </summary>
    protected bool Reject(PeerIndex from, PeerState state, DisconnectReason reason)
    {
        state.ConnectionState = PeerConnectionState.PENDING_DISCONNECT;
        violationMetric.Add(1);
        transport.Disconnect(from, reason);
        return false;
    }
}
