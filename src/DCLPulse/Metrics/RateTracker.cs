namespace Pulse.Metrics;

/// <summary>
///     Tracks a monotonic counter, computes per-second rate, and maintains rolling-window
///     and lifetime percentiles. Not thread-safe — used only from the timer callback.
/// </summary>
internal sealed class RateTracker(int percentileWindow)
{
    private readonly PercentileBuffer window = new (percentileWindow);
    private readonly LifetimePercentileBuffer lifetime = new ();
    private long prevTotal;

    public RateStats Update(long currentTotal, double elapsedSec)
    {
        double rate = (currentTotal - prevTotal) / elapsedSec;
        prevTotal = currentTotal;
        window.Add(rate);
        lifetime.Add(rate);

        return new RateStats
        {
            PerSec = rate,
            Window = window.ToPercentileStats(),
            Lifetime = lifetime.ToPercentileStats(),
        };
    }
}
