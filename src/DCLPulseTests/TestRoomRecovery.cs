using Pulse.Clusters;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Runtime.CompilerServices;

namespace DCLPulseTests;

internal static class TestRoomRecovery
{
    private static readonly ConditionalWeakTable<ClusterBoard, IdentityBoard> IDENTITIES = new();

    internal static IdentityBoard Identities(ClusterBoard board) => IDENTITIES.GetValue(board, static _ => new IdentityBoard(100));

    internal static void PublishRecoveryAssignmentsForTest(this ClusterBoard board, IReadOnlyDictionary<string, ClusterAssignment> assignments)
    {
        IdentityBoard identities = Identities(board);
        foreach (RoomRecoveryAssignment previous in board.RoomRecoveryAssignments.Values)
            if (previous.Peer is { } peer) identities.Remove(peer);

        var recovery = new Dictionary<string, RoomRecoveryAssignment>(StringComparer.Ordinal);
        var slot = 0u;
        foreach ((string wallet, ClusterAssignment assignment) in assignments)
        {
            var peer = new PeerIndex(slot++);
            identities.Set(peer, wallet, assignment.Session);
            recovery.Add(wallet, new RoomRecoveryAssignment(assignment, peer, identities.GetIdentity(peer),
                "test-epoch", "1", false, false, true, 0, []));
        }

        board.PublishAssignments(assignments);
        board.PublishRoomRecovery(recovery, new RoomRecoveryStatus("test-epoch", true, 0, recovery.Count));
    }
}
