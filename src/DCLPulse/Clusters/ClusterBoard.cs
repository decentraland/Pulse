using System.Numerics;
using Pulse.Peers;

namespace Pulse.Clusters;

/// <summary>
///     Metadata for one cluster in a single tracker pass. <see cref="Centroid" /> is the mean of
///     member positions and <see cref="Radius" /> the distance from it to the farthest member —
///     the same geometry archipelago reported, so <c>engine.islands</c> stays shape-compatible.
/// </summary>
public readonly record struct ClusterInfo(
    string Id,
    string Realm,
    int Count,
    Vector3 Centroid,
    float Radius
);

/// <summary>
///     Per-peer detail for one member of a cluster, carried so the stats surface can serve
///     wallet ↔ position ↔ parcel ↔ cluster from the last pass at zero marginal cost.
/// </summary>
public readonly record struct ClusterPeerInfo(
    PeerIndex Peer,
    string Wallet,
    string ClusterId,
    string Realm,
    Vector3 Position,
    int Parcel
);

/// <summary>
///     The immutable result of one clustering pass. Never mutated after construction, so any
///     number of readers can hold it for as long as they like while the tracker builds the next.
/// </summary>
public sealed class ClusterPass(
    IReadOnlyList<ClusterInfo> clusters,
    IReadOnlyList<ClusterPeerInfo> peers,
    string?[] clusterIdByPeer)
{
    public static readonly ClusterPass EMPTY = new ([], [], []);

    // Indexed by PeerIndex; null where the peer is unassigned. Gives readers O(1) "which cluster
    // is this peer in" without scanning Peers.

    public IReadOnlyList<ClusterInfo> Clusters { get; } = clusters;

    public IReadOnlyList<ClusterPeerInfo> Peers { get; } = peers;

    /// <summary>
    ///     The cluster this peer belonged to as of this pass, or null if it was unassigned
    ///     (no realm, or not present in the grid when the pass ran).
    /// </summary>
    public string? GetClusterId(PeerIndex peer)
    {
        var index = (int)peer.Value;

        return index < clusterIdByPeer.Length ? clusterIdByPeer[index] : null;
    }
}

/// <summary>
///     Holds the latest <see cref="ClusterPass" />. Single writer (the tracker thread) swaps the
///     whole result in with one <see cref="Volatile.Write{T}" />; readers are lock-free and always
///     observe a complete, self-consistent pass — never a half-built one.
///     <para />
///     A board separate from <c>SnapshotBoard</c> is the sanctioned exception to the
///     ledger-column rule: this state is globally derived rather than per-peer, has exactly one
///     writer, and is never mutated by a worker.
/// </summary>
public sealed class ClusterBoard
{
    private ClusterPass current = ClusterPass.EMPTY;

    public ClusterPass Current =>
        Volatile.Read(ref current);

    public void Publish(ClusterPass pass)
    {
        Volatile.Write(ref current, pass);
    }
}
