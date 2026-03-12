using Microsoft.Extensions.Options;
using System.Numerics;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.InterestManagement;

/// <summary>
///     Spatial-hash-based interest management. Queries the <see cref="SpatialGrid" /> with a
///     single linear scan over all peer slots, filtering by the observer's 3×3 cell neighbourhood.
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

        Span<long> adjacent = stackalloc long[9];
        var n = 0;
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
            adjacent[n++] = grid.ComputeCellKey(
                new Vector3(observerPos.X + (dx * cellSize), 0, observerPos.Z + (dz * cellSize)));

        int maxPeers = grid.MaxPeers;

        for (uint i = 0; i < (uint)maxPeers; i++)
        {
            long key = grid.ReadCellKey(new PeerIndex(i));
            if (key == SpatialGrid.NO_CELL) continue;

            var inRange = false;

            for (var j = 0; j < 9; j++)
                if (adjacent[j] == key)
                {
                    inRange = true;
                    break;
                }

            if (!inRange) continue;

            var subject = new PeerIndex(i);
            if (subject == observer) continue;

            if (!snapshotBoard.TryRead(subject, out PeerSnapshot subjectSnapshot))
                continue;

            float distX = subjectSnapshot.Position.X - observerPos.X;
            float distZ = subjectSnapshot.Position.Z - observerPos.Z;
            float distSq = (distX * distX) + (distZ * distZ);

            if (distSq > maxDistanceSq) continue;

            PeerViewSimulationTier tier = distSq <= tier0Sq ? PeerViewSimulationTier.TIER_0 :
                distSq <= tier1Sq ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

            collector.Add(subject, tier);
        }
    }
}
