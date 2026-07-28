namespace DCLPulseBenchmarks;

/// <summary>
///     Population shapes the clustering pass is measured against.
///     <para />
///     The first three are the worked examples from <c>docs/island-clustering-on-aoi.md</c> §3.2,
///     reproduced from their published scale and shape — peer counts, region sizes, whether neighbours
///     bridge or stay split — because the original coordinates were never recorded.
///     <see cref="ClusterTrackerBenchmarks" /> prints the realized topology at setup, so drift from the
///     documented figures is visible rather than assumed.
/// </summary>
public enum ClusterScenario
{
    /// <summary>
    ///     Sparse steady state, 100 peers: two areas close enough to bridge into one cluster of 32, a
    ///     second pair far enough apart to stay split, a duo, and many loners. The common case, where
    ///     archipelago and Pulse agreed exactly.
    /// </summary>
    Sporadic,

    /// <summary>
    ///     1 000 peers: a 450-peer crowd plus 10 sparser regions, two pairs of which merge — the
    ///     shape that produced 9 Pulse clusters against archipelago's 14, where the 100-peer cap
    ///     sliced the crowd into 5 overlapping rooms.
    /// </summary>
    DenseAndSparse,

    /// <summary>
    ///     Chaining worst case, 1 000 peers: 10 areas of 100 in a line over ~1.2 km, each bridging into
    ///     the next, so the whole line is one cluster spanning most of the occupied cells.
    /// </summary>
    Chained,

    /// <summary>
    ///     A uniform Genesis City fill at <c>Transport.MaxPeers</c>: the worst case a single instance can
    ///     reach, and the figure to quote for capacity headroom.
    /// </summary>
    CeilingUniform,
}
