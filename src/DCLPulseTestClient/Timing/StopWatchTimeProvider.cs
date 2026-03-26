using System.Diagnostics;

namespace PulseTestClient.Timing;

public class StopWatchTimeProvider : ITimeProvider
{
    private readonly long startupTime = Stopwatch.GetTimestamp();

    public uint TimeSinceStartupMs => (uint) Stopwatch.GetElapsedTime(startupTime).TotalMilliseconds;
}