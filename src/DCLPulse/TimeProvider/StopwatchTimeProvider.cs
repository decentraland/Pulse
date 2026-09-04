using System.Diagnostics;

namespace Pulse;

public class StopwatchTimeProvider : ITimeProvider
{
    private readonly long startTimestamp = Stopwatch.GetTimestamp();

    // Wall clock read once, at the same instant as the monotonic origin above, so every wall-clock
    // answer below is that origin plus monotonic elapsed. Reading DateTimeOffset.UtcNow per call
    // instead would let an NTP step or a DST-free clock adjustment move timestamps backwards
    // relative to the ticks they are derived from.
    private readonly long startUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public uint MonotonicTime => (uint)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    // Full elapsed rather than MonotonicTime: that property truncates to 32 bits and would fold the
    // wall clock back ~49.7 days into the past on a long-lived process.
    public long UnixTimeMs => startUnixMs + (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    public long ToUnixTimeMs(uint monotonicTime) => startUnixMs + monotonicTime;
}
