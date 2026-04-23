using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Token-bucket rate limiter shared across the server's discrete-event handlers (emote
///     start/stop, teleport). Whole-token refills at one token per <see cref="refillIntervalMs" />
///     up to <see cref="burstCapacity" />. On violation the peer is disconnected with
///     <see cref="DisconnectReason.DISCRETE_EVENT_RATE_EXCEEDED" /> — violations are client
///     bugs, not back-pressure. Per-peer state lives on <see cref="PeerThrottleState" /> (byte
///     tokens + uint timestamp); invoked on the owning worker thread so there is no
///     concurrent access.
/// </summary>
public sealed class DiscreteEventRateLimiter
{
    private readonly byte burstCapacity;
    private readonly uint refillIntervalMs;
    private readonly ITimeProvider timeProvider;
    private readonly ITransport transport;

    public DiscreteEventRateLimiter(
        IOptions<DiscreteEventRateLimiterOptions> options,
        ITimeProvider timeProvider,
        ITransport transport)
    {
        this.timeProvider = timeProvider;
        this.transport = transport;

        int cap = options.Value.BurstCapacity;
        burstCapacity = (byte)Math.Clamp(cap, 0, byte.MaxValue);

        refillIntervalMs = options.Value.RatePerSecond > 0
            ? (uint)Math.Max(1, 1000.0 / options.Value.RatePerSecond)
            : 0u;
    }

    public bool IsEnabled => refillIntervalMs > 0 && burstCapacity > 0;

    /// <summary>
    ///     Returns <c>true</c> if the event is allowed. On rejection, disconnects
    ///     <paramref name="from" /> and returns <c>false</c>. On success the peer's bucket is
    ///     debited by one; on failure the bucket is untouched except for the refill timestamp.
    /// </summary>
    public bool TryAccept(PeerIndex from, PeerState state)
    {
        if (!IsEnabled) return true;

        uint now = timeProvider.MonotonicTime;
        PeerThrottleState t = state.Throttle;

        byte tokens = t.DiscreteEventTokens;
        uint last = t.DiscreteEventLastRefillMs;
        uint newLast;

        if (last == 0)
        {
            // First event from this peer — start with a full bucket.
            tokens = burstCapacity;
            newLast = now;
        }
        else
        {
            uint refills = (now - last) / refillIntervalMs;

            if (refills > 0)
            {
                tokens = (byte)Math.Min((uint)burstCapacity, (uint)tokens + refills);
                // Preserve sub-interval remainder so accuracy doesn't drift.
                newLast = last + (refills * refillIntervalMs);
            }
            else
            {
                newLast = last;
            }
        }

        if (tokens == 0)
        {
            state.Throttle = t with
            {
                DiscreteEventTokens = 0,
                DiscreteEventLastRefillMs = newLast,
            };
            PulseMetrics.Hardening.DISCRETE_EVENT_THROTTLED.Add(1);
            transport.Disconnect(from, DisconnectReason.DISCRETE_EVENT_RATE_EXCEEDED);
            return false;
        }

        state.Throttle = t with
        {
            DiscreteEventTokens = (byte)(tokens - 1),
            DiscreteEventLastRefillMs = newLast,
        };
        return true;
    }
}
