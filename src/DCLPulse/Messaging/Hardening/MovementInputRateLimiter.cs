using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Caps <c>PlayerStateInput</c> admission to at most <c>MaxHz</c> per peer by enforcing a
///     minimum interval between accepted inputs. On violation the peer is disconnected with
///     <see cref="DisconnectReason.INPUT_RATE_EXCEEDED" /> — violations are client bugs, not
///     back-pressure. Per-peer timestamp lives on <see cref="PeerThrottleState.LastInputMs" />;
///     invoked on the owning worker thread so no synchronisation is needed.
/// </summary>
public sealed class MovementInputRateLimiter(
    IOptions<MovementInputRateLimiterOptions> options,
    ITimeProvider timeProvider,
    ITransport transport)
    : PeerDefense(transport, PulseMetrics.Hardening.INPUT_RATE_THROTTLED)
{
    private readonly int maxHz = options.Value.MaxHz;
    private readonly uint minIntervalMs = options.Value.MaxHz > 0 ? (uint)(1000 / options.Value.MaxHz) : 0u;

    public bool IsEnabled => maxHz > 0;

    /// <summary>
    ///     Returns <c>true</c> if the input should be processed. On rejection, disconnects
    ///     <paramref name="from" /> and returns <c>false</c> — the caller must skip the
    ///     message. Mutates <see cref="PeerState.Throttle" /> to stamp the accepted timestamp.
    /// </summary>
    public bool TryAccept(PeerIndex from, PeerState state)
    {
        if (!IsEnabled) return true;

        uint now = timeProvider.MonotonicTime;
        uint last = state.Throttle.LastInputMs;

        if (last != 0 && now - last < minIntervalMs)
            return Reject(from, state, DisconnectReason.INPUT_RATE_EXCEEDED);

        // 0 is reserved as the "never" sentinel — clamp the stamp so a peer whose first input
        // arrives at server time 0 can't re-use the sentinel on subsequent calls.
        state.Throttle = state.Throttle with { LastInputMs = now == 0 ? 1u : now };
        return true;
    }
}
