using Microsoft.Extensions.Options;
using System.Numerics;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.InterestManagement;

/// <summary>
///     Spatial-hash-based interest management. Reads from <see cref="RealmSpatialGrids" />, which is
///     maintained incrementally on the write path. Queries only check the observer's cell and its
///     neighbors — no full scan of all active peers.
///     <para />
///     Realm partitioning is structural, not a predicate: each realm has its own
///     <see cref="SpatialGrid" />, so resolving the observer's grid is the whole of it and no candidate
///     is ever tested for realm equality. A peer whose latest <see cref="PeerSnapshot" /> has no realm
///     — neither seeded at handshake nor teleported yet — occupies no grid, so it is invisible to every
///     observer and sees nobody.
///     <para />
///     Thread-safe: all reads are lock-free. The grids are updated by workers on the write path.
/// </summary>
public sealed class SpatialHashAreaOfInterest : IAreaOfInterest
{
    private readonly RealmSpatialGrids realmGrids;
    private readonly SnapshotBoard snapshotBoard;
    private readonly float tier0Sq;
    private readonly float tier1Sq;
    private readonly float maxDistanceSq;
    private readonly int scanCellRadius;

    public SpatialHashAreaOfInterest(RealmSpatialGrids realmGrids,
        SnapshotBoard snapshotBoard,
        IOptions<SpatialHashAreaOfInterestOptions> optionsContainer)
    {
        this.realmGrids = realmGrids;
        this.snapshotBoard = snapshotBoard;

        SpatialHashAreaOfInterestOptions options = optionsContainer.Value;
        tier0Sq = options.Tier0Radius * options.Tier0Radius;
        tier1Sq = options.Tier1Radius * options.Tier1Radius;
        maxDistanceSq = options.MaxRadius * options.MaxRadius;
        scanCellRadius = options.ScanCellRadius;
    }

    public void GetVisibleSubjects(PeerIndex observer, in PeerSnapshot observerSnapshot, IInterestCollector collector)
    {
        SpatialGrid? grid = realmGrids.GetGrid(observerSnapshot.Realm);

        if (grid == null)
            return;

        Vector3 observerPos = observerSnapshot.GlobalPosition;
        realmGrids.CellCoords(observerPos, out int cellX, out int cellZ);

        for (int dx = -scanCellRadius; dx <= scanCellRadius; dx++)
            for (int dz = -scanCellRadius; dz <= scanCellRadius; dz++)
                Collect(observer, observerPos, collector, grid.GetPeers(SpatialGrid.PackKey(cellX + dx, cellZ + dz)));
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

            float distX = subjectSnapshot.GlobalPosition.X - observerPos.X;
            float distZ = subjectSnapshot.GlobalPosition.Z - observerPos.Z;
            float distSq = (distX * distX) + (distZ * distZ);

            if (distSq > maxDistanceSq)
                continue;

            PeerViewSimulationTier tier = distSq <= tier0Sq ? PeerViewSimulationTier.TIER_0 :
                distSq <= tier1Sq ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

            collector.Add(subject, tier);
        }
    }
}
