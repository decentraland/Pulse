using Microsoft.Extensions.Options;
using System.Numerics;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.InterestManagement;

/// <summary>
///     Spatial-hash-based interest management. Reads from <see cref="SpatialGrid" /> which is
///     maintained incrementally on the write path. Queries only check the observer's cell
///     and its neighbors — no full scan of all active peers.
///     <para />
///     Thread-safe: all reads are lock-free. The grid is updated by workers on the write path.
/// </summary>
public sealed class SpatialHashAreaOfInterest : IAreaOfInterest
{
    private readonly SpatialGrid grid;
    private readonly SnapshotBoard snapshotBoard;
    private readonly float tier0Sq;
    private readonly float tier1Sq;
    private readonly float maxDistanceSq;
    private readonly float cellSize;

    public SpatialHashAreaOfInterest(SpatialGrid grid,
        SnapshotBoard snapshotBoard,
        IOptions<SpatialHashAreaOfInterestOptions> optionsContainer)
    {
        this.grid = grid;
        this.snapshotBoard = snapshotBoard;

        SpatialHashAreaOfInterestOptions options = optionsContainer.Value;
        tier0Sq = options.Tier0Radius * options.Tier0Radius;
        tier1Sq = options.Tier1Radius * options.Tier1Radius;
        maxDistanceSq = options.MaxRadius * options.MaxRadius;
        cellSize = options.CellSize;
    }

    public void GetVisibleSubjects(PeerIndex observer, in PeerSnapshot observerSnapshot, IInterestCollector collector)
    {
        Vector3 observerPos = observerSnapshot.Position;

        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
        {
            var cell = new Vector3(observerPos.X + (dx * cellSize), 0, observerPos.Z + (dz * cellSize));

            Collect(observer, observerPos, collector, grid.GetPeers(cell));
        }
    }

    private void Collect(PeerIndex observer, Vector3 observerPos, IInterestCollector collector, HashSet<PeerIndex>? peers)
    {
        if (peers == null)
            return;

        foreach (PeerIndex subject in peers)
        {
            if (subject == observer)
                continue;

            if (!snapshotBoard.TryRead(subject, out PeerSnapshot subjectSnapshot))
                continue;

            float distX = subjectSnapshot.Position.X - observerPos.X;
            float distZ = subjectSnapshot.Position.Z - observerPos.Z;
            float distSq = (distX * distX) + (distZ * distZ);

            if (distSq > maxDistanceSq)
                continue;

            PeerViewSimulationTier tier = distSq <= tier0Sq ? PeerViewSimulationTier.TIER_0 :
                distSq <= tier1Sq ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

            collector.Add(subject, tier);
        }
    }
}
