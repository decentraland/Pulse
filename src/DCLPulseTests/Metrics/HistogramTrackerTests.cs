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

    [Test]
    public void Merge_sums_counts_and_preserves_bounds()
    {
        // Real callers share one bounds array instance (every BucketHistogram snapshot carries the
        // same RTT_BUCKETS_MS reference), which Merge asserts — so the parts share `bounds` here too.
        long[] bounds = [10L, 20L];
        var a = new HistogramSnapshot { UpperBounds = bounds, Counts = [1L, 0L, 2L], Count = 3, Sum = 45 };
        var b = new HistogramSnapshot { UpperBounds = bounds, Counts = [0L, 4L, 0L], Count = 4, Sum = 60 };

        HistogramSnapshot merged = HistogramSnapshots.Merge([a, default, b]);

        Assert.That(merged.UpperBounds, Is.EqualTo(new long[] { 10, 20 }));
        Assert.That(merged.Counts, Is.EqualTo(new long[] { 1, 4, 2 }));
        Assert.That(merged.Count, Is.EqualTo(7));
        Assert.That(merged.Sum, Is.EqualTo(105));
    }

    [Test]
    public void Merge_of_null_or_all_default_is_default()
    {
        Assert.That(HistogramSnapshots.Merge(null).Count, Is.EqualTo(0));
        Assert.That(HistogramSnapshots.Merge([default, default]).Counts, Is.Null);
    }

    // Bounds equal by value but distinct by reference — the layout mismatch a future caller could
    // introduce by mixing histograms. Merge must reject it loudly (now an always-on guard, not a
    // Debug.Assert compiled out of Release) rather than summing incompatible bucket edges.
    [Test]
    public void Merge_throws_when_parts_use_distinct_bounds_arrays()
    {
        var a = new HistogramSnapshot { UpperBounds = [10L, 20L], Counts = [1L, 0L, 2L], Count = 3, Sum = 45 };
        var b = new HistogramSnapshot { UpperBounds = [10L, 20L], Counts = [0L, 4L, 0L], Count = 4, Sum = 60 };

        Assert.That(() => HistogramSnapshots.Merge([a, b]), Throws.ArgumentException);
    }
}
