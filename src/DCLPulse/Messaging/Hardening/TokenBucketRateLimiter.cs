using Pulse.Peers;
using Pulse.Transport;
using System.Diagnostics.Metrics;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Shared token-bucket implementation used by the post-auth per-message limiters
///     (movement input, discrete events). Whole-token refills at one token per
///     <see cref="refillIntervalMs" /> up to <see cref="burstCapacity" />. On violation the
///     peer is disconnected with the configured <see cref="rejectReason" /> — violations are
///     client bugs, not back-pressure. Per-peer state lives on <see cref="PeerThrottleState" />
///     in a slot the derived class wires up via <see cref="GetBucket" /> /
///     <see cref="SetBucket" />; invoked on the owning worker thread so there is no
///     concurrent access.
/// </summary>
public abstract class TokenBucketRateLimiter : PeerDefense
{
    private readonly byte burstCapacity;
    private readonly uint refillIntervalMs;
    private readonly DisconnectReason rejectReason;
    private readonly ITimeProvider timeProvider;

    protected TokenBucketRateLimiter(
        int burstCapacity,
        uint refillIntervalMs,
        DisconnectReason rejectReason,
        ITimeProvider timeProvider,
        ITransport transport,
        Counter<long> violationMetric)
        : base(transport, violationMetric)
    {
        this.burstCapacity = (byte)Math.Clamp(burstCapacity, 0, byte.MaxValue);
        this.refillIntervalMs = refillIntervalMs;
        this.rejectReason = rejectReason;
        this.timeProvider = timeProvider;
    }

    public bool IsEnabled => refillIntervalMs > 0 && burstCapacity > 0;

    /// <summary>Read the bucket slot for this limiter out of the shared throttle state.</summary>
    protected abstract (byte tokens, uint lastRefillMs) GetBucket(PeerThrottleState t);

    /// <summary>Write the bucket slot for this limiter back into the shared throttle state.</summary>
    protected abstract PeerThrottleState SetBucket(PeerThrottleState t, byte tokens, uint lastRefillMs);

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
        (byte tokens, uint last) = GetBucket(t);
        uint newLast;

        if (last == 0)
        {
            // First event from this peer — start with a full bucket. Clamp 0 to 1 so the
            // sentinel doesn't collide with an actual MonotonicTime reading of 0.
            tokens = burstCapacity;
            newLast = now == 0 ? 1u : now;
        }
        else
        {
            uint refills = (now - last) / refillIntervalMs;

            if (refills > 0)
            {
                tokens = (byte)Math.Min(burstCapacity, tokens + refills);

                // Preserve sub-interval remainder so accuracy doesn't drift.
                newLast = last + (refills * refillIntervalMs);
            }
            else { newLast = last; }
        }

        if (tokens == 0)
        {
            state.Throttle = SetBucket(t, 0, newLast);
            return Reject(from, state, rejectReason);
        }

        state.Throttle = SetBucket(t, (byte)(tokens - 1), newLast);
        return true;
    }
}
