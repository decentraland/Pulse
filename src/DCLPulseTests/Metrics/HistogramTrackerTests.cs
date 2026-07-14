using Pulse.Metrics;
using Pulse.Metrics.Console;

namespace DCLPulseTests.Metrics;

[TestFixture]
public class HistogramTrackerTests
{
    private static HistogramSnapshot Snap(long[] counts, long count) =>
        new ()
        {
            UpperBounds = [10L, 20L],
            Counts = counts,
            Count = count,
            Sum = 0,
        };

    [Test]
    public void Window_percentiles_reflect_only_the_delta_since_previous_snapshot()
    {
        var tracker = new HistogramTracker();

        // First snapshot: 1 sample in bucket 0 → lifetime P50 = 10 (interpolated to bucket top).
        RateStats first = tracker.Update(Snap([1, 0, 0], 1), elapsedSec: 1.0);
        Assert.That(first.PerSec, Is.EqualTo(1.0));
        Assert.That(first.Lifetime.P50, Is.EqualTo(10.0));

        // Second snapshot: 5 new samples all in bucket 1 → window sees only those 5.
        // Window P50: rank ceil(0.5*5)=3 of 5 in (10..20] → 10 + 10*3/5 = 16.
        RateStats second = tracker.Update(Snap([1, 5, 0], 6), elapsedSec: 1.0);
        Assert.That(second.PerSec, Is.EqualTo(5.0));
        Assert.That(second.Window.P50, Is.EqualTo(16.0));

        // Lifetime still spans all 6: rank 3 of 6 → bucket 1, 2 of 5 into it → 14.
        Assert.That(second.Lifetime.P50, Is.EqualTo(14.0));
    }

    [Test]
    public void Default_snapshot_yields_default_stats()
    {
        var tracker = new HistogramTracker();

        RateStats stats = tracker.Update(default(HistogramSnapshot), elapsedSec: 1.0);

        Assert.That(stats.PerSec, Is.EqualTo(0.0));
        Assert.That(stats.Window.P99, Is.EqualTo(0.0));
    }
}
