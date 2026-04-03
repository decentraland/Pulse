namespace Pulse.Metrics;

/// <summary>
///     Tracks a sampled gauge value with rolling-window and lifetime percentiles.
///     Not thread-safe — used only from the timer callback.
/// </summary>
internal sealed class GaugeTracker(int percentileWindow)
{
    private readonly PercentileBuffer window = new (percentileWindow);
    private readonly LifetimePercentileBuffer lifetime = new ();

    public RateStats Record(double value)
    {
        window.Add(value);
        lifetime.Add(value);

        return new RateStats
        {
            PerSec = value,
            Window = window.ToPercentileStats(),
            Lifetime = lifetime.ToPercentileStats(),
        };
    }
}
