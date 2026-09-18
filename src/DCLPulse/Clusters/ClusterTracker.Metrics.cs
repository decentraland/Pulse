using Pulse.Metrics;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pulse.Clusters;

/// <summary>
///     What one pass reports to <see cref="PulseMetrics.Clusters" />: its cost and outcome.
/// </summary>
public sealed partial class ClusterTracker
{
    // Last value published for each gauge. An up-down counter takes a delta, not an absolute.
    private int lastClusterCount;
    private int lastClusterPeers;
    private int lastSizeMax;

    private void RecordPassMetrics(long startTicks, int clusterCount, int reassignments)
    {
        PulseMetrics.Clusters.PASSES.Add(1);
        PulseMetrics.Clusters.PASS_DURATION_US.Add((long)Stopwatch.GetElapsedTime(startTicks).TotalMicroseconds);

        if (reassignments > 0)
            PulseMetrics.Clusters.REASSIGNMENTS.Add(reassignments);

        RecordClusterSizes(out int peers, out int largest);

        RecordGauge(PulseMetrics.Clusters.COUNT, clusterCount, ref lastClusterCount);
        RecordGauge(PulseMetrics.Clusters.PEERS, peers, ref lastClusterPeers);
        RecordGauge(PulseMetrics.Clusters.SIZE_MAX, largest, ref lastSizeMax);
    }

    /// <summary>
    ///     Records one histogram observation per cluster and returns the two totals the histogram cannot
    ///     answer: how many peers were clustered at all, and the largest cluster. Reads
    ///     <see cref="PassComponent.MemberCount" /> rather than the built <see cref="ClusterPass" />, so
    ///     it is independent of how the pass is materialized.
    /// </summary>
    private void RecordClusterSizes(out int peers, out int largest)
    {
        peers = 0;
        largest = 0;

        for (var component = 0; component < components.Count; component++)
        {
            int size = components[component].MemberCount;

            PulseMetrics.Clusters.SIZE.Record(size);

            peers += size;
            largest = Math.Max(largest, size);
        }
    }

    /// <summary>
    ///     Publishes an absolute gauge value through an up-down counter, which takes a delta.
    /// </summary>
    private static void RecordGauge(UpDownCounter<int> gauge, int value, ref int previous)
    {
        gauge.Add(value - previous);
        previous = value;
    }
}
