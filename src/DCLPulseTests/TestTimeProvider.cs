using Pulse;

namespace DCLPulseTests;

/// <summary>
///     An <see cref="ITimeProvider" /> whose clocks are set rather than substituted, for tests that
///     have to pin the exact wall-clock values that reach the wire — a batch's <c>server_time</c>, a
///     pass's <c>lastUpdated</c>, a peer's <c>lastPing</c>.
///     <para />
///     The two clocks stay coupled exactly as <c>StopwatchTimeProvider</c> couples them: the wall
///     clock is <see cref="UnixOriginMs" /> plus monotonic elapsed, so a stamp translated by
///     <see cref="ToUnixTimeMs" /> and a <see cref="UnixTimeMs" /> read of the same instant agree.
///     Setting <see cref="UnixTimeMs" /> therefore moves the monotonic clock, and a value below the
///     origin has no monotonic time to move to — <see cref="MonotonicTime" /> is unsigned — so it
///     fails loudly instead of wrapping into an unrelated timestamp.
/// </summary>
internal sealed class TestTimeProvider : ITimeProvider
{
    /// <summary>Origin of the wall clock: the unix ms that monotonic 0 corresponds to.</summary>
    public long UnixOriginMs { get; set; }

    public uint MonotonicTime { get; set; }

    public long UnixTimeMs
    {
        get => UnixOriginMs + MonotonicTime;

        set
        {
            long elapsed = value - UnixOriginMs;

            if (elapsed is < 0 or > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"unix time is {elapsed} ms from this provider's origin ({UnixOriginMs}), which is not a monotonic time — set UnixOriginMs first");

            MonotonicTime = (uint)elapsed;
        }
    }

    public long ToUnixTimeMs(uint monotonicTime) =>
        UnixOriginMs + monotonicTime;
}
