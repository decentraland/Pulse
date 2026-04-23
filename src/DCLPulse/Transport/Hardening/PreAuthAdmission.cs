using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;

namespace Pulse.Transport.Hardening;

/// <summary>
///     Admission control for peers entering PENDING_AUTH. Enforces two caps simultaneously:
///     a global budget (<see cref="PreAuthAdmissionOptions.PreAuthBudget" />) that reserves the
///     bulk of the PeerIndex pool for authenticated peers, and a per-source-IP quota
///     (<see cref="PreAuthAdmissionOptions.MaxConcurrentPreAuthPerIP" />) that prevents a single
///     IP from holding a disproportionate share of that budget.
///     <para />
///     Threading: <see cref="TryAdmit" /> is called from the ENet thread on Connect. Both
///     release methods are called from the owning worker thread — <see cref="ReleaseOnPromotion" />
///     when the handshake validates, <see cref="ReleaseOnDisconnect" /> from the peer's
///     lifecycle Disconnected event (covers client drop, handshake failure, auth-timeout). A
///     single lock guards both counters so they can never drift; contention is bounded by the
///     connect/disconnect rate, not packet rate.
/// </summary>
public sealed class PreAuthAdmission(IOptions<PreAuthAdmissionOptions> options)
{
    public enum AdmitResult
    {
        OK,
        IP_LIMIT_EXHAUSTED,
        BUDGET_EXHAUSTED,
    }

    private readonly Lock syncRoot = new ();
    private readonly Dictionary<string, int> perIpCounts = new ();
    private readonly Dictionary<PeerIndex, string> ipByPendingPeer = new ();
    private readonly int perIpCap = options.Value.MaxConcurrentPreAuthPerIP;
    private readonly int globalBudget = options.Value.PreAuthBudget;
    private int inFlight;

    public int InFlight
    {
        get { lock (syncRoot) return inFlight; }
    }

    public AdmitResult TryAdmit(PeerIndex peerIndex, string ip)
    {
        lock (syncRoot)
        {
            perIpCounts.TryGetValue(ip, out int perIp);

            if (perIpCap > 0 && perIp >= perIpCap)
            {
                PulseMetrics.Hardening.PRE_AUTH_IP_LIMIT_REFUSED.Add(1);
                return AdmitResult.IP_LIMIT_EXHAUSTED;
            }

            if (globalBudget > 0 && inFlight >= globalBudget)
            {
                PulseMetrics.Hardening.PRE_AUTH_REFUSED.Add(1);
                return AdmitResult.BUDGET_EXHAUSTED;
            }

            // Both checks pass — commit atomically under the lock so the two counters can never
            // disagree about the number of admitted peers.
            perIpCounts[ip] = perIp + 1;
            ipByPendingPeer[peerIndex] = ip;
            inFlight++;
            PulseMetrics.Hardening.PRE_AUTH_IN_FLIGHT.Add(1);
            return AdmitResult.OK;
        }
    }

    /// <summary>
    ///     Called from a worker thread when a peer's handshake validates (PENDING_AUTH →
    ///     AUTHENTICATED). The peer's slot — global and per-IP — is returned to the pool.
    /// </summary>
    public void ReleaseOnPromotion(PeerIndex peerIndex) => ReleaseInternal(peerIndex);

    /// <summary>
    ///     Called from a worker thread on the peer's lifecycle Disconnected event. Idempotent
    ///     with respect to <see cref="ReleaseOnPromotion" />: if the peer already authenticated,
    ///     the lookup misses and nothing is decremented.
    /// </summary>
    public void ReleaseOnDisconnect(PeerIndex peerIndex) => ReleaseInternal(peerIndex);

    private void ReleaseInternal(PeerIndex peerIndex)
    {
        lock (syncRoot)
        {
            if (!ipByPendingPeer.Remove(peerIndex, out string? ip)) return;

            if (perIpCounts.TryGetValue(ip, out int c))
            {
                if (c <= 1)
                    perIpCounts.Remove(ip);
                else
                    perIpCounts[ip] = c - 1;
            }

            inFlight--;
            PulseMetrics.Hardening.PRE_AUTH_IN_FLIGHT.Add(-1);
        }
    }
}
