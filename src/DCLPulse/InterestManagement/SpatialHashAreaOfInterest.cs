using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.InterestManagement;

/// <summary>
///     Spatial-hash-based interest management. Reads from <see cref="RealmSpatialGrids" />, which is
///     maintained incrementally on the write path. Player queries inspect neighboring cells;
///     listener queries inspect cells covering their announced parcels.
///     <para />
///     Realm grids supply candidates. A retained cell set can outlive a teleport, so each candidate's
///     snapshot is checked against the queried realm and spatial limits before acceptance. Accepted
///     sequences carry the same identity registration observed before and after their snapshot read.
///     <para />
///     Thread-safe: all reads are lock-free. The grids are updated by workers on the write path.
/// </summary>
public sealed class SpatialHashAreaOfInterest : IAreaOfInterest
{
    private readonly RealmSpatialGrids realmGrids;
    private readonly SnapshotBoard snapshotBoard;
    private readonly IdentityBoard identityBoard;
    private readonly float tier0Sq;
    private readonly float tier1Sq;
    private readonly float maxDistanceSq;
    private readonly int scanCellRadius;

    public SpatialHashAreaOfInterest(RealmSpatialGrids realmGrids,
        SnapshotBoard snapshotBoard,
        IdentityBoard identityBoard,
        IOptions<SpatialHashAreaOfInterestOptions> optionsContainer)
    {
        this.realmGrids = realmGrids;
        this.snapshotBoard = snapshotBoard;
        this.identityBoard = identityBoard;

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
                CollectPlayerSubjects(observer, in observerSnapshot, collector,
                    grid.GetPeers(SpatialGrid.PackKey(cellX + dx, cellZ + dz)));
    }

    public void GetVisibleSubjects(PeerIndex observer, SceneListenerState listener, IInterestCollector collector)
    {
        foreach ((string realm, HashSet<int> parcels) in listener.ParcelsByRealm)
        {
            SpatialGrid? grid = realmGrids.GetGrid(realm);

            if (grid == null)
                continue;

            foreach (long cellKey in listener.CellKeys)
                CollectListenerSubjects(observer, realm, parcels, collector, grid.GetPeers(cellKey));
        }
    }

    private void CollectPlayerSubjects(PeerIndex observer, in PeerSnapshot observerSnapshot,
        IInterestCollector collector, HashSet<PeerIndex>? peers)
    {
        if (peers == null)
            return;

        foreach (PeerIndex subject in peers)
        {
            if (subject == observer)
                continue;

            if (!TryReadRegisteredSnapshot(subject, out PeerSnapshot subjectSnapshot, out IdentityRegistration? identity))
                continue;

            if (!string.Equals(subjectSnapshot.Realm, observerSnapshot.Realm, StringComparison.Ordinal))
                continue;

            float distX = subjectSnapshot.GlobalPosition.X - observerSnapshot.GlobalPosition.X;
            float distZ = subjectSnapshot.GlobalPosition.Z - observerSnapshot.GlobalPosition.Z;
            float distSq = (distX * distX) + (distZ * distZ);

            if (distSq > maxDistanceSq)
                continue;

            PeerViewSimulationTier tier = distSq <= tier0Sq ? PeerViewSimulationTier.TIER_0 :
                distSq <= tier1Sq ? PeerViewSimulationTier.TIER_1 : PeerViewSimulationTier.TIER_2;

            collector.Add(subject, tier, subjectSnapshot.Seq, identity);
        }
    }

    private void CollectListenerSubjects(PeerIndex observer, string realm, HashSet<int> parcels,
        IInterestCollector collector, HashSet<PeerIndex>? peers)
    {
        if (peers == null)
            return;

        foreach (PeerIndex subject in peers)
        {
            if (subject == observer)
                continue;

            if (!TryReadRegisteredSnapshot(subject, out PeerSnapshot subjectSnapshot, out IdentityRegistration? identity))
                continue;

            if (!string.Equals(subjectSnapshot.Realm, realm, StringComparison.Ordinal) || !parcels.Contains(subjectSnapshot.Parcel))
                continue;

            collector.Add(subject, PeerViewSimulationTier.TIER_0, subjectSnapshot.Seq, identity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadRegisteredSnapshot(PeerIndex subject, out PeerSnapshot snapshot,
        [NotNullWhen(true)] out IdentityRegistration? identity)
    {
        identity = identityBoard.GetIdentity(subject);

        if (identity == null)
        {
            snapshot = default;
            return false;
        }

        return snapshotBoard.TryRead(subject, out snapshot) && ReferenceEquals(identity, identityBoard.GetIdentity(subject));
    }
}
