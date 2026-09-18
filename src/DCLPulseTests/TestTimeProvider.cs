using Pulse;

namespace DCLPulseTests;

/// <summary>
///     An <see cref="ITimeProvider" /> whose clocks are set rather than substituted, for tests that
///     pin the exact wall-clock values reaching the wire (<c>server_time</c>, <c>lastUpdated</c>,
///     <c>lastPing</c>). Wall clock is <see cref="UnixOriginMs" /> plus monotonic elapsed, as
///     <c>StopwatchTimeProvider</c> couples them, so setting <see cref="UnixTimeMs" /> moves the
///     monotonic clock and a value below the origin throws rather than wrapping.
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
