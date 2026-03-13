using System.Numerics;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.InterestManagement;

public sealed class SpatialAreaOfInterest(SnapshotBoard snapshotBoard, SpatialAreaOfInterestOptions options) : IAreaOfInterest
{
    private readonly float tier0Sq = options.Tier0Radius * options.Tier0Radius;
    private readonly float tier1Sq = options.Tier1Radius * options.Tier1Radius;
    private readonly float maxDistanceSq = options.MaxRadius * options.MaxRadius;

    public void GetVisibleSubjects(PeerIndex observer, in PeerSnapshot observerSnapshot, IInterestCollector collector)
    {
        Vector3 observerPos = observerSnapshot.GlobalPosition;

        foreach (PeerIndex subject in snapshotBoard.GetActivePeers())
        {
            if (subject == observer)
                continue;

            if (!snapshotBoard.TryRead(subject, out PeerSnapshot subjectSnapshot))
                continue;

            float dx = subjectSnapshot.GlobalPosition.X - observerPos.X;
            float dz = subjectSnapshot.GlobalPosition.Z - observerPos.Z;
            float distSq = (dx * dx) + (dz * dz);

            if (distSq > maxDistanceSq)
                continue;

            PeerViewSimulationTier tier = distSq <= tier0Sq ? PeerViewSimulationTier.TIER_0
                : distSq <= tier1Sq ? PeerViewSimulationTier.TIER_1
                : PeerViewSimulationTier.TIER_2;

            collector.Add(subject, tier);
        }
    }
}
