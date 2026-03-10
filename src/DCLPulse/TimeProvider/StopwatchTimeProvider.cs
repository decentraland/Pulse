using System.Diagnostics;

namespace Pulse;

public class StopwatchTimeProvider : ITimeProvider
{
    private readonly long startTimestamp = Stopwatch.GetTimestamp();

    public uint MonotonicTime => (uint)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
