using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Stats;

/// <summary>
///     One consistent read of the boards, projected into the shapes the stats routes answer with.
///     Everything comes off a single <see cref="ClusterPass" /> taken once per request, so no two
///     fields of one response come from different instants and <c>/realms</c>' <c>lastUpdated</c> is
///     the age of the pass, not the time of the reply.
///     <para />
///     <c>lastPing</c> is the exception, read per peer from <see cref="SnapshotBoard" />. A peer that
///     left between the pass and this read has no snapshot, so it falls back to the pass time rather
///     than dropping out of a list that says it is there.
/// </summary>
public sealed class StatsBoardView
{
    private readonly PeerResult[] peers;
    private readonly string[] clusterIds;
    private readonly ClusterPass pass;

    // clusterId -> its peers in address order, built on the first island request of this view. Lazy
    // because most routes never ask: /peers, /parcels, /realms and /status read the flat array, and
    // rescanning it per island would be O(peers x clusters) per request.
    private Dictionary<string, List<PeerResult>>? membersByCluster;

    private StatsBoardView(ClusterPass pass, PeerResult[] peers, string[] clusterIds)
    {
        this.pass = pass;
        this.peers = peers;
        this.clusterIds = clusterIds;
    }

    /// <summary>Unix ms of the pass this view was built from; zero when no pass has run yet.</summary>
    public long TakenAtUnixMs => pass.TakenAtUnixMs;

    /// <summary>Peers on this server, across every realm — <c>/about</c>'s <c>userCount</c>.</summary>
    public int UserCount => peers.Length;

    public static StatsBoardView Read(
        ClusterBoard clusterBoard,
        SnapshotBoard snapshotBoard,
        ParcelEncoder parcelEncoder,
        ITimeProvider timeProvider)
    {
        ClusterPass pass = clusterBoard.Current;
        int count = pass.Peers.Count;

        var peers = new PeerResult[count];
        var clusterIds = new string[count];
        var order = new int[count];

        for (var i = 0; i < count; i++)
        {
            ClusterPeerInfo info = pass.Peers[i];

            parcelEncoder.Decode(info.Parcel, out int x, out int z);

            long lastPing = snapshotBoard.TryRead(info.Peer, out PeerSnapshot snapshot)
                ? timeProvider.ToUnixTimeMs(snapshot.ServerTick)
                : pass.TakenAtUnixMs;

            // Already lowercase in production — AuthChainValidator normalizes before IdentityBoard —
            // so this is defensive, and free when it holds.
            string address = CanonicalName.Of(info.Wallet);

            peers[i] = new PeerResult(
                address, address, lastPing,
                [x, z],
                [info.Position.X, info.Position.Y, info.Position.Z],
                info.Realm);

            clusterIds[i] = info.ClusterId;
            order[i] = i;
        }

        // Sorted once by address, the order every peers list in C2 is specified in. The parallel
        // cluster-id array is permuted with it so membership stays keyed to its own peer.
        Array.Sort(order, (a, b) => string.CompareOrdinal(peers[a].Address, peers[b].Address));

        var sortedPeers = new PeerResult[count];
        var sortedClusterIds = new string[count];

        for (var i = 0; i < count; i++)
        {
            sortedPeers[i] = peers[order[i]];
            sortedClusterIds[i] = clusterIds[order[i]];
        }

        return new StatsBoardView(pass, sortedPeers, sortedClusterIds);
    }

    /// <summary>
    ///     Every realm holding at least one peer, peers descending then name ascending. A realm has
    ///     no existence apart from the peers in it, so an empty one is simply absent.
    /// </summary>
    public IReadOnlyList<RealmSummary> Realms()
    {
        var peerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var clusterCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (PeerResult peer in peers)
            peerCounts[peer.Realm!] = peerCounts.GetValueOrDefault(peer.Realm!) + 1;

        foreach (ClusterInfo cluster in pass.Clusters)
            if (peerCounts.ContainsKey(cluster.Realm))
                clusterCounts[cluster.Realm] = clusterCounts.GetValueOrDefault(cluster.Realm) + 1;

        return peerCounts
              .Select(entry => new RealmSummary(entry.Key, entry.Value, clusterCounts.GetValueOrDefault(entry.Key)))
              .OrderByDescending(static realm => realm.Peers)
              .ThenBy(static realm => realm.Name, StringComparer.Ordinal)
              .ToArray();
    }

    /// <summary>The peers of one realm, entries dropping the realm the envelope carries.</summary>
    public IReadOnlyList<PeerResult> PeersIn(string realm) =>
        peers.Where(peer => string.Equals(peer.Realm, realm, StringComparison.Ordinal))
             .Select(static peer => peer with { Realm = null })
             .ToArray();

    /// <summary>Every peer, each carrying its realm, since there is no envelope to carry it.</summary>
    public IReadOnlyList<PeerResult> AllPeers() => peers;

    /// <summary>
    ///     The peers whose address is in <paramref name="addresses" />, matched case-insensitively so
    ///     EIP-55 checksummed wallets match. Ids that match nothing are simply absent.
    /// </summary>
    public IReadOnlyList<PeerResult> PeersMatching(IReadOnlyCollection<string> addresses)
    {
        var wanted = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);

        return peers.Where(peer => wanted.Contains(peer.Address)).ToArray();
    }

    public PeerResult? Peer(string address) =>
        peers.FirstOrDefault(peer => string.Equals(peer.Address, address, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Occupied parcels of one realm, busiest first then by coordinate. Scoping is not optional:
    ///     every world numbers its parcels from (0,0), so an unscoped count would merge worlds.
    /// </summary>
    public IReadOnlyList<ParcelCount> ParcelsIn(string realm)
    {
        var counts = new Dictionary<(int X, int Y), int>();

        foreach (PeerResult peer in peers)
        {
            if (!string.Equals(peer.Realm, realm, StringComparison.Ordinal)) continue;

            (int, int) key = (peer.Parcel[0], peer.Parcel[1]);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts
              .Select(static entry => new ParcelCount(entry.Value, new ParcelPoint(entry.Key.X, entry.Key.Y)))
              .OrderByDescending(static parcel => parcel.PeersCount)
              .ThenBy(static parcel => parcel.Parcel.X)
              .ThenBy(static parcel => parcel.Parcel.Y)
              .ToArray();
    }

    /// <summary>
    ///     The clusters of one realm in natural id order, members by address. Ids come from one
    ///     global counter, so they are unique across realms.
    /// </summary>
    public IReadOnlyList<IslandResult> IslandsIn(string realm)
    {
        var islands = new List<IslandResult>();

        foreach (ClusterInfo cluster in pass.Clusters)
        {
            if (!string.Equals(cluster.Realm, realm, StringComparison.Ordinal)) continue;

            islands.Add(BuildIsland(cluster));
        }

        islands.Sort(static (a, b) => NaturalIdOrder(a.Id, b.Id));

        return islands;
    }

    public IslandResult? IslandIn(string realm, string islandId)
    {
        foreach (ClusterInfo cluster in pass.Clusters)
            if (string.Equals(cluster.Realm, realm, StringComparison.Ordinal)
             && string.Equals(cluster.Id, islandId, StringComparison.Ordinal))
                return BuildIsland(cluster);

        return null;
    }

    private IslandResult BuildIsland(ClusterInfo cluster) =>
        new (
            cluster.Id,
            MaxPeers: 0,
            [cluster.Centroid.X, cluster.Centroid.Y, cluster.Centroid.Z],
            cluster.Radius,
            MembersOf(cluster.Id));

    /// <summary>
    ///     The members of one cluster, in address order. Indexed in one walk of the peer array on the
    ///     first island request, so a request costs one pass rather than one per island.
    /// </summary>
    private IReadOnlyList<PeerResult> MembersOf(string clusterId)
    {
        if (membersByCluster is null)
        {
            membersByCluster = new Dictionary<string, List<PeerResult>>(StringComparer.Ordinal);

            // The parallel arrays are already in address order, so members need no second sort.
            for (var i = 0; i < peers.Length; i++)
            {
                if (!membersByCluster.TryGetValue(clusterIds[i], out List<PeerResult>? members))
                    membersByCluster[clusterIds[i]] = members = [];

                members.Add(peers[i] with { Realm = null });
            }
        }

        return membersByCluster.GetValueOrDefault(clusterId) ?? (IReadOnlyList<PeerResult>)[];
    }

    /// <summary>
    ///     C1 &lt; C2 &lt; C10, which ordinal ordering gets wrong. Ids are
    ///     <c>{Clusters:IdPrefix}{n}</c>, so the comparison is on the numeric tail; anything without
    ///     one falls back to ordinal, since the prefix is configurable.
    /// </summary>
    private static int NaturalIdOrder(string left, string right)
    {
        if (TryNumericTail(left, out int leftNumber) && TryNumericTail(right, out int rightNumber))
            return leftNumber.CompareTo(rightNumber);

        return string.CompareOrdinal(left, right);
    }

    private static bool TryNumericTail(string id, out int number)
    {
        var start = 0;

        while (start < id.Length && !char.IsAsciiDigit(id[start]))
            start++;

        return int.TryParse(id.AsSpan(start), out number);
    }
}
