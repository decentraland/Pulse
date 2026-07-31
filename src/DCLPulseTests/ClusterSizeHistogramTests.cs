using Pulse.Metrics;

namespace DCLPulseTests;

/// <summary>
///     Bucket layout for the cluster-size histogram. The collector indexes with it and the formatter
///     labels with it, so a boundary being off by one mislabels every series downstream and silently
///     skews every query-time quantile.
/// </summary>
[TestFixture]
public class ClusterSizeHistogramTests
{
    [Test]
    public void Bounds_AreAscendingAndPositive()
    {
        Assert.That(ClusterSizeHistogram.BOUNDS, Is.Ordered.Ascending);
        Assert.That(ClusterSizeHistogram.BOUNDS, Is.All.GreaterThan(0));
    }

    [Test]
    public void BucketCount_LeavesRoomForTheOverflowSlot()
    {
        // Prometheus requires a +Inf bucket; the collector's array has to have somewhere to put it.
        Assert.That(ClusterSizeHistogram.BUCKET_COUNT, Is.EqualTo(ClusterSizeHistogram.BOUNDS.Length + 1));
    }

    [Test]
    public void TopBound_CoversTheLargestReachableCluster()
    {
        // A cluster cannot exceed Transport.MaxPeers (4095 in appsettings), so nothing should normally
        // land in the overflow slot. If MaxPeers is ever raised past the top bound, this fails first.
        Assert.That(ClusterSizeHistogram.BOUNDS[^1], Is.GreaterThanOrEqualTo(4095));
    }

    [TestCase(1, 0)]
    [TestCase(2, 1)]
    [TestCase(3, 2)]
    [TestCase(4, 2)]
    [TestCase(5, 3)]
    [TestCase(8, 3)]
    [TestCase(9, 4)]
    [TestCase(4096, 12)]
    public void IndexOf_PicksTheFirstBoundTheValueDoesNotExceed(int size, int expectedIndex)
    {
        Assert.That(ClusterSizeHistogram.IndexOf(size), Is.EqualTo(expectedIndex));
    }

    [Test]
    public void IndexOf_BoundaryValueLandsInItsOwnBucket_NotTheNextOne()
    {
        // `le` is inclusive: a cluster of exactly 64 belongs to le="64", not le="128".
        int boundary = ClusterSizeHistogram.BOUNDS[6];

        Assert.That(boundary, Is.EqualTo(64));
        Assert.That(ClusterSizeHistogram.IndexOf(boundary), Is.EqualTo(6));
        Assert.That(ClusterSizeHistogram.IndexOf(boundary + 1), Is.EqualTo(7));
    }

    [Test]
    public void IndexOf_AboveTheTopBound_UsesTheOverflowSlot()
    {
        int overflow = ClusterSizeHistogram.BOUNDS[^1] + 1;

        Assert.That(ClusterSizeHistogram.IndexOf(overflow), Is.EqualTo(ClusterSizeHistogram.BOUNDS.Length));
    }

    [Test]
    public void EveryBoundIndexesToItsOwnSlot()
    {
        for (var i = 0; i < ClusterSizeHistogram.BOUNDS.Length; i++)
            Assert.That(ClusterSizeHistogram.IndexOf(ClusterSizeHistogram.BOUNDS[i]), Is.EqualTo(i),
                $"bound {ClusterSizeHistogram.BOUNDS[i]} must index to slot {i}");
    }
}
