using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Token-bucket rate limiter shared across the server's discrete-event handlers (emote
///     start/stop, teleport). Each event triggers an O(observers) reliable broadcast, so the
///     cap bounds the fan-out amplification an attacker can drive. Inherits its core logic
///     from <see cref="TokenBucketRateLimiter" />.
/// </summary>
public sealed class DiscreteEventRateLimiter : TokenBucketRateLimiter
{
    public DiscreteEventRateLimiter(
        IOptions<DiscreteEventRateLimiterOptions> options,
        ITimeProvider timeProvider,
        ITransport transport)
        : base(
            options.Value.BurstCapacity,
            options.Value.RatePerSecond > 0 ? (uint)Math.Max(1, 1000.0 / options.Value.RatePerSecond) : 0u,
            DisconnectReason.DISCRETE_EVENT_RATE_EXCEEDED,
            timeProvider,
            transport,
            PulseMetrics.Hardening.DISCRETE_EVENT_THROTTLED) { }

    protected override (byte tokens, uint lastRefillMs) GetBucket(PeerThrottleState t) =>
        (t.DiscreteEventTokens, t.DiscreteEventLastRefillMs);

    protected override PeerThrottleState SetBucket(PeerThrottleState t, byte tokens, uint lastRefillMs) =>
        t with { DiscreteEventTokens = tokens, DiscreteEventLastRefillMs = lastRefillMs };
}
