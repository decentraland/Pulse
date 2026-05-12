using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Caps <c>PlayerStateInput</c> admission to <c>MaxHz</c> per peer with a small burst
///     allowance, sharing the <see cref="TokenBucketRateLimiter" /> implementation with
///     <see cref="DiscreteEventRateLimiter" />. The burst absorbs UDP jitter — uniformly
///     paced client sends routinely arrive in tight clusters after ISP/NAT/Wi-Fi queueing,
///     which a strict-interval check would flag as a violation.
/// </summary>
public sealed class MovementInputRateLimiter : TokenBucketRateLimiter
{
    public MovementInputRateLimiter(
        IOptions<MovementInputRateLimiterOptions> options,
        ITimeProvider timeProvider,
        ITransport transport)
        : base(
            options.Value.BurstCapacity,
            options.Value.MaxHz > 0 ? (uint)Math.Max(1, 1000 / options.Value.MaxHz) : 0u,
            DisconnectReason.INPUT_RATE_EXCEEDED,
            timeProvider,
            transport,
            PulseMetrics.Hardening.INPUT_RATE_THROTTLED) { }

    protected override (byte tokens, uint lastRefillMs) GetBucket(PeerThrottleState t) =>
        (t.InputTokens, t.InputLastRefillMs);

    protected override PeerThrottleState SetBucket(PeerThrottleState t, byte tokens, uint lastRefillMs) =>
        t with { InputTokens = tokens, InputLastRefillMs = lastRefillMs };
}
