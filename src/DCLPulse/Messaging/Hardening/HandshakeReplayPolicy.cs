using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Sliding-window cache of accepted <c>(wallet, timestamp)</c> handshake pairs. Rejects
///     duplicates within the PENDING_AUTH window so a captured handshake packet can't be
///     replayed against the same server instance while the original peer is still in-flight.
///     <para />
///     Both knobs are derived rather than duplicated:
///     TTL = <see cref="PeerOptions.PendingAuthCleanTimeoutMs" /> (single source of truth for
///     how long PENDING_AUTH state lives) and memory cap =
///     <see cref="ENetTransportOptions.MaxPeers" /> (connects in flight can't exceed the
///     PeerIndex pool, so the cache needs no more).
///     <para />
///     Called once per Connect on a worker thread. Shared across workers — same wallet can
///     hit any worker depending on <see cref="PeerIndex" /> allocation — so the dictionary is
///     guarded by a single lock. Contention is bounded by the handshake rate, not the packet
///     rate, so a lock is cheap here.
/// </summary>
public sealed class HandshakeReplayPolicy(
    IOptions<HandshakeReplayPolicyOptions> options,
    PeerOptions peerOptions,
    IOptions<ENetTransportOptions> transportOptions,
    ITimeProvider timeProvider,
    ITransport transport)
    : PeerDefense(transport, PulseMetrics.Hardening.HANDSHAKE_REPLAY_REJECTED)
{
    private readonly uint ttlMs = peerOptions.PendingAuthCleanTimeoutMs;
    private readonly int maxEntries = transportOptions.Value.MaxPeers;
    private readonly Lock syncRoot = new ();
    private readonly Dictionary<(string Wallet, string Timestamp), uint> seen = new ();

    public bool IsEnabled
    {
        get => field && ttlMs > 0;
    } = options.Value.Enabled;

    /// <summary>
    ///     Records the pair if it's fresh; rejects the peer and returns <c>false</c> if the
    ///     same pair was admitted earlier within the TTL window. On rejection the peer is
    ///     disconnected with <see cref="DisconnectReason.HANDSHAKE_REPLAY_REJECTED" />.
    /// </summary>
    public bool TryAdmit(PeerIndex from, PeerState state, string wallet, string timestamp)
    {
        if (!IsEnabled) return true;

        uint now = timeProvider.MonotonicTime;
        uint expiry = now + ttlMs;
        var key = (wallet, timestamp);

        lock (syncRoot)
        {
            if (seen.TryGetValue(key, out uint existingExpiry) && existingExpiry > now)
                return Reject(from, state, DisconnectReason.HANDSHAKE_REPLAY_REJECTED);

            // Opportunistic sweep when the cache starts getting large. Walking the dict is
            // O(n) but n is bounded by the handshake rate × TTL, and sweeping only triggers
            // when we're approaching the memory cap.
            if (seen.Count >= maxEntries / 2)
                SweepExpired(now);

            // After sweep, only insert if we're still under the hard cap. Overflow means a
            // handshake flood; we validate without replay protection rather than refuse new
            // handshakes, since pruning old entries can't create room for a current attacker.
            if (seen.Count < maxEntries)
                seen[key] = expiry;
        }

        return true;
    }

    private void SweepExpired(uint now)
    {
        var expired = new List<(string, string)>();

        foreach (((string, string) k, uint exp) in seen)
            if (exp <= now)
                expired.Add(k);

        foreach ((string, string) k in expired)
            seen.Remove(k);
    }
}
