using Pulse.Metrics;

namespace DCLPulseTests.Metrics;

[TestFixture]
public class BucketHistogramTests
{
    private static readonly long[] BOUNDS = [10, 20, 30];

    [Test]
    public void Record_routes_values_to_the_correct_bucket()
    {
        var histogram = new BucketHistogram(BOUNDS);

        histogram.Record(5);   // ≤10 → bucket 0
        histogram.Record(10);  // ==10 is inclusive → bucket 0
        histogram.Record(11);  // ≤20 → bucket 1
        histogram.Record(31);  // > all bounds → +Inf bucket

        HistogramSnapshot snap = histogram.Snapshot();

        Assert.That(snap.Counts, Is.EqualTo(new long[] { 2, 1, 0, 1 }));
        Assert.That(snap.Count, Is.EqualTo(4));
        Assert.That(snap.Sum, Is.EqualTo(5 + 10 + 11 + 31));
    }

    [Test]
    public void Percentile_interpolates_within_the_containing_bucket()
    {
        var histogram = new BucketHistogram(BOUNDS);

        histogram.Record(5);
        histogram.Record(15);
        histogram.Record(25);

        HistogramSnapshot snap = histogram.Snapshot();

        // rank = ceil(0.5 * 3) = 2 → bucket 1 (10..20], 1 of 1 into the bucket → 20.
        Assert.That(snap.Percentile(0.50), Is.EqualTo(20.0));

        // rank = ceil(0.99 * 3) = 3 → bucket 2 (20..30], 1 of 1 → 30.
        Assert.That(snap.Percentile(0.99), Is.EqualTo(30.0));
    }

    [Test]
    public void Percentile_of_empty_histogram_is_zero()
    {
        var histogram = new BucketHistogram(BOUNDS);

        Assert.That(histogram.Snapshot().Percentile(0.99), Is.EqualTo(0.0));
    }

    [Test]
    public void Percentile_landing_in_overflow_bucket_reports_last_finite_bound()
    {
        var histogram = new BucketHistogram(BOUNDS);

        histogram.Record(1000);

        Assert.That(histogram.Snapshot().Percentile(0.99), Is.EqualTo(30.0));
    }

    [Test]
    public void Percentile_of_default_snapshot_is_zero()
    {
        Assert.That(default(HistogramSnapshot).Percentile(0.99), Is.EqualTo(0.0));
    }

    [Test]
    public void Record_is_thread_safe()
    {
        var histogram = new BucketHistogram(BOUNDS);

        Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < 100_000; i++)
                histogram.Record(i % 40);
        });

        HistogramSnapshot snap = histogram.Snapshot();

        // 4 threads × 100k records of (i % 40). Each thread cycles 0..39 exactly 2500 times.
        Assert.That(snap.Count, Is.EqualTo(400_000));

        // Per-thread sum = 2500 × Σ(0..39) = 2500 × 780 = 1_950_000; ×4 threads = 7_800_000.
        Assert.That(snap.Sum, Is.EqualTo(7_800_000));

        // Bounds [10,20,30], value > bound → next bucket (== bound stays). Per 40-cycle:
        // bucket0 = 0..10 (11), bucket1 = 11..20 (10), bucket2 = 21..30 (10), +Inf = 31..39 (9).
        // ×2500 cycles ×4 threads → [110_000, 100_000, 100_000, 90_000].
        Assert.That(snap.Counts, Is.EqualTo(new long[] { 110_000, 100_000, 100_000, 90_000 }));
    }
}
