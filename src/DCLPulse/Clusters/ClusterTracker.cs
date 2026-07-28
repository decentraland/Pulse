using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Pulse.Clusters;

/// <summary>
///     Derives cluster membership from the same boards area-of-interest reads, on its own thread and
///     wholly off the hot path: one pass every <see cref="ClusterOptions.PassIntervalMs" /> that
///     reads <see cref="SpatialGrid" /> and <see cref="SnapshotBoard" /> and never touches a worker,
///     a peer state dict or <c>PeerSimulation</c>.
///     <para />
///     A pass is weighted union-find with path halving over occupied grid cells using 8-neighbor
///     adjacency, with nodes keyed <c>(realm, cellKey)</c> so a cluster never spans realms. Cost is
///     O(N + C) in peers and occupied cells — no peer-pair tests. Clustering on cells rather than
///     distances makes the effective join range follow the AoI <c>CellSize</c>: one spatial
///     resolution for both visibility and clustering.
///     <para />
///     Working buffers are fields and are cleared, not reallocated, between passes. The published
///     <see cref="ClusterPass" /> is necessarily fresh each time — readers hold it by reference.
/// </summary>
public sealed class ClusterTracker : BackgroundService
{
    // Absent link — no component, no next node, no claimant.
    private const int NONE = -1;

    // Forward half of the 8-neighborhood: the +X column plus the cell straight ahead in +Z. Every
    // node probes and union is symmetric, so each adjacent pair is still visited exactly once, from
    // whichever of the two cells holds the other at one of these offsets. Half the lookups of the
    // full ring for an identical partition, and the probe is the bulk of a pass.
    private static readonly int[] NEIGHBOR_DX = [1, 1, 1, 0];
    private static readonly int[] NEIGHBOR_DZ = [-1, 0, 1, 1];

    private readonly ILogger<ClusterTracker> logger;
    private readonly ClusterOptions options;
    private readonly SpatialGrid spatialGrid;
    private readonly SnapshotBoard snapshotBoard;
    private readonly IdentityBoard identityBoard;
    private readonly ClusterBoard clusterBoard;
    private readonly IClusterFeedPublisher feedPublisher;

    // Per-pass realm interning. A node key carries a dense realm id rather than the realm name, so
    // a neighbor probe hashes two integers instead of re-hashing a client-supplied string. Rebuilt
    // every pass: realm names arrive on the wire, so a table carried across passes would grow
    // without bound.
    private readonly List<string> realmNames = [];
    private readonly Dictionary<string, int> realmIdByName = new ();

    // Per-pass cell graph. One node per (realm, cell), carrying its slice of members and its own
    // union-find state.
    private readonly Dictionary<NodeKey, int> nodeIndexByKey = new ();
    private readonly List<PassNode> nodes = [];

    // Per-pass members, ordered so every node's members are one contiguous slice.
    private readonly List<PassMember> members = [];

    // Per-pass components, each a chain of the nodes that union-find merged into it.
    private readonly List<PassComponent> components = [];

    // Scratch for one component's overlap tally against the previous pass. Cleared per component,
    // and holds one entry per previous cluster the component draws members from — a handful.
    private readonly Dictionary<string, int> overlapCounts = new ();

    // State carried across passes, owned solely by this thread.
    private readonly PeerClusterState[] peerStates;
    private readonly Dictionary<string, ClusterRecord> clusterRecords = new ();

    private long passNumber;
    private long nextClusterNumber;
    private int lastClusterCount;

    public ClusterTracker(
        ILogger<ClusterTracker> logger,
        IOptions<ClusterOptions> options,
        SpatialGrid spatialGrid,
        SnapshotBoard snapshotBoard,
        IdentityBoard identityBoard,
        ClusterBoard clusterBoard,
        IClusterFeedPublisher feedPublisher,
        int maxPeers)
    {
        this.logger = logger;
        this.options = options.Value;
        this.spatialGrid = spatialGrid;
        this.snapshotBoard = snapshotBoard;
        this.identityBoard = identityBoard;
        this.clusterBoard = clusterBoard;
        this.feedPublisher = feedPublisher;

        peerStates = new PeerClusterState[maxPeers];
    }

    // A List indexer hands back a copy of a struct element, so in-place updates go through the
    // backing store instead. Properties rather than cached locals: re-reading costs a couple of
    // instructions and cannot go stale when the list grows mid-loop.
    private Span<PassNode> NodeSpan =>
        CollectionsMarshal.AsSpan(nodes);

    private Span<PassComponent> ComponentSpan =>
        CollectionsMarshal.AsSpan(components);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Cluster tracker disabled (Clusters:Enabled is false)");
            return;
        }

        if (options.PassIntervalMs <= 0)
        {
            logger.LogWarning("Cluster tracker disabled (Clusters:PassIntervalMs is not positive)");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(options.PassIntervalMs);

        logger.LogInformation(
            "Cluster tracker started — pass every {PassIntervalMs}ms, dwell {DwellPasses} passes, id prefix {IdPrefix}",
            options.PassIntervalMs, options.DwellPasses, options.IdPrefix);

        // Long-running so the pass never occupies a thread-pool worker.
        await Task.Factory.StartNew(
            () => RunPassLoop(interval, stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunPassLoop(TimeSpan interval, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunPass(); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogError(e, "Cluster pass failed; retaining previous assignments until the next pass");
            }

            try { Task.Delay(interval, stoppingToken).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    ///     One clustering pass, start to finish. Each step is isolated so it can be reasoned about
    ///     and tested on its own.
    ///     <para />
    ///     Internal rather than private so tests can drive passes deterministically instead of
    ///     racing the background loop's timer.
    /// </summary>
    internal void RunPass()
    {
        long startTicks = Stopwatch.GetTimestamp();

        // Stamps every per-pass liveness check in this class: a peer slot, a cluster record and a
        // claim are all "current" exactly when they carry this number, so nothing has to be cleared
        // to mark it stale.
        passNumber++;

        CollectNodes();
        UnionNeighbors();
        GroupComponents();
        AssignStickyIds();

        ClusterPass pass = BuildPass();

        RememberComputedAssignments();
        clusterBoard.Publish(pass);
        int reassignments = PublishAssignmentChanges();
        ForgetVanishedPeers();

        feedPublisher.PublishTopology(pass);
        RecordPassMetrics(startTicks, pass.Clusters.Count, reassignments);
    }

    private void RecordPassMetrics(long startTicks, int clusterCount, int reassignments)
    {
        PulseMetrics.Clusters.PASSES.Add(1);
        PulseMetrics.Clusters.PASS_DURATION_US.Add((long)Stopwatch.GetElapsedTime(startTicks).TotalMicroseconds);

        if (reassignments > 0)
            PulseMetrics.Clusters.REASSIGNMENTS.Add(reassignments);

        // Reported as a delta so the collector accumulates it like any other up-down counter.
        PulseMetrics.Clusters.COUNT.Add(clusterCount - lastClusterCount);
        lastClusterCount = clusterCount;
    }

    /// <summary>
    ///     Reads occupied cells and builds one node per <c>(realm, cell)</c> pair.
    /// </summary>
    private void CollectNodes()
    {
        realmNames.Clear();
        realmIdByName.Clear();
        nodeIndexByKey.Clear();
        nodes.Clear();
        members.Clear();

        foreach (SpatialGrid.OccupiedCell cell in spatialGrid.GetOccupiedCells())
        {
            int cellFirstMember = members.Count;

            foreach (PeerIndex peer in cell.Occupants)
                TryCollectMember(peer);

            GroupCellMembersByRealm(cell.Key, cellFirstMember);
        }
    }

    /// <summary>
    ///     Resolves one occupant into a <see cref="PassMember" />, or skips it. Peers whose snapshot
    ///     is unreadable, whose realm is unset, or whose wallet is unknown are skipped — they cannot
    ///     be placed in a realm-partitioned cluster nor addressed on the feed. A peer already
    ///     collected this pass is skipped too: the grid read is weakly consistent, so a peer that
    ///     changes cell mid-enumeration can surface in both its old and its new cell, and every
    ///     later step assumes a peer appears at most once.
    /// </summary>
    private void TryCollectMember(PeerIndex peer)
    {
        ref PeerClusterState state = ref peerStates[peer.Value];

        if (state.LastSeenPass == passNumber) return;
        if (!snapshotBoard.TryRead(peer, out PeerSnapshot snapshot)) return;
        if (snapshot.Realm is null) return;

        string? wallet = identityBoard.GetWalletIdByPeerIndex(peer);

        if (wallet is null) return;

        state.LastSeenPass = passNumber;

        members.Add(new PassMember(
            peer, wallet, snapshot.Realm, snapshot.GlobalPosition, snapshot.Parcel, snapshot.IsTeleport));
    }

    /// <summary>
    ///     Partitions the members just appended for one cell into a node per distinct realm,
    ///     reordering them in place so every node's members are contiguous in
    ///     <see cref="members" />. One cell almost always holds a single realm, so this settles
    ///     into a single linear scan.
    /// </summary>
    private void GroupCellMembersByRealm(long cellKey, int cellFirstMember)
    {
        int cursor = cellFirstMember;

        while (cursor < members.Count)
        {
            string realm = members[cursor].Realm;
            int groupStart = cursor;
            cursor++;

            // Pull every remaining member of the same realm up next to the group.
            for (int scan = cursor; scan < members.Count; scan++)
            {
                if (!string.Equals(members[scan].Realm, realm, StringComparison.Ordinal)) continue;

                (members[cursor], members[scan]) = (members[scan], members[cursor]);
                cursor++;
            }

            AddNode(new NodeKey(InternRealm(realm), cellKey), groupStart, cursor - groupStart);
        }
    }

    /// <summary>
    ///     Maps a realm name to its dense id for this pass, assigning one if the realm is new.
    /// </summary>
    private int InternRealm(string realm)
    {
        if (realmIdByName.TryGetValue(realm, out int id)) return id;

        id = realmNames.Count;
        realmNames.Add(realm);
        realmIdByName[realm] = id;

        return id;
    }

    private void AddNode(NodeKey key, int memberStart, int memberCount)
    {
        int index = nodes.Count;

        nodeIndexByKey[key] = index;

        nodes.Add(new PassNode
        {
            Key = key,
            MemberStart = memberStart,
            MemberCount = memberCount,
            Parent = index,
            TreeSize = 1,
            Component = NONE,
            NextInComponent = NONE,
        });
    }

    /// <summary>
    ///     Unions each node with its same-realm neighbors. Two peers in adjacent cells are between 0
    ///     and <c>2 * CellSize * sqrt(2)</c> apart — 0–283 units at the configured 100-unit cell size
    ///     — a much coarser proxy for archipelago's 64-unit join distance, with the resulting
    ///     boundary noise absorbed by the dwell debounce rather than by a second distance threshold.
    /// </summary>
    private void UnionNeighbors()
    {
        Span<PassNode> table = NodeSpan;

        for (var node = 0; node < table.Length; node++)
        {
            NodeKey key = table[node].Key;
            SpatialGrid.UnpackKey(key.CellKey, out int x, out int z);

            for (var i = 0; i < NEIGHBOR_DX.Length; i++)
            {
                var neighborKey = new NodeKey(key.RealmId, SpatialGrid.PackKey(x + NEIGHBOR_DX[i], z + NEIGHBOR_DZ[i]));

                if (nodeIndexByKey.TryGetValue(neighborKey, out int neighbor))
                    Union(table, node, neighbor);
            }
        }
    }

    private static int Find(Span<PassNode> table, int node)
    {
        while (table[node].Parent != node)
        {
            // Path halving — compresses without a second traversal.
            table[node].Parent = table[table[node].Parent].Parent;
            node = table[node].Parent;
        }

        return node;
    }

    private static void Union(Span<PassNode> table, int a, int b)
    {
        int rootA = Find(table, a);
        int rootB = Find(table, b);

        if (rootA == rootB) return;

        // Weighted: the smaller tree hangs off the larger, keeping Find shallow.
        if (table[rootA].TreeSize < table[rootB].TreeSize)
            (rootA, rootB) = (rootB, rootA);

        table[rootB].Parent = rootA;
        table[rootA].TreeSize += table[rootB].TreeSize;
    }

    /// <summary>
    ///     Turns union-find roots into dense components.
    /// </summary>
    private void GroupComponents()
    {
        components.Clear();

        Span<PassNode> table = NodeSpan;

        for (var node = 0; node < table.Length; node++)
            AttachToComponent(table, node);
    }

    /// <summary>
    ///     Files one node under its union-find root's component, creating that component the first
    ///     time the root is reached and pushing the node onto its chain. The chain is what lets a
    ///     component's members be walked without a separate ordering array: a component is its
    ///     nodes, and a node's members are already contiguous. Only a root node's
    ///     <see cref="PassNode.Component" /> is meaningful.
    /// </summary>
    private void AttachToComponent(Span<PassNode> table, int node)
    {
        int root = Find(table, node);
        int component = table[root].Component;

        if (component == NONE)
        {
            component = components.Count;

            // Union only ever merges same-realm nodes, so any node of the component names its realm.
            components.Add(new PassComponent
            {
                RealmId = table[node].Key.RealmId,
                FirstNode = NONE,
                Id = string.Empty,
            });

            table[root].Component = component;
        }

        // Taken after the Add above, which would have invalidated an earlier span.
        Span<PassComponent> componentTable = ComponentSpan;

        table[node].NextInComponent = componentTable[component].FirstNode;
        componentTable[component].FirstNode = node;
        componentTable[component].MemberCount += table[node].MemberCount;
    }

    /// <summary>
    ///     Gives each component the ID of the previous cluster it shares the most members with,
    ///     so a crowd that splits or merges keeps a stable identity across passes. Ties resolve to
    ///     the older cluster. When two components claim the same previous ID the one with the larger
    ///     overlap keeps it and the other takes a fresh ID.
    /// </summary>
    private void AssignStickyIds()
    {
        for (var component = 0; component < components.Count; component++)
            FindBestInheritedId(component);

        for (var component = 0; component < components.Count; component++)
            ResolveInheritanceConflict(component);

        Span<PassComponent> table = ComponentSpan;

        for (var component = 0; component < table.Length; component++)
            table[component].Id = table[component].InheritedId ?? MintClusterId();

        PruneVanishedClusters();
    }

    private void FindBestInheritedId(int component)
    {
        overlapCounts.Clear();

        foreach (PassMember member in MembersOf(component))
        {
            string? previous = peerStates[member.Peer.Value].PreviousPassClusterId;

            if (previous is null) continue;

            overlapCounts[previous] = overlapCounts.GetValueOrDefault(previous) + 1;
        }

        string? bestId = null;
        var bestCount = 0;
        long bestCreationSeq = long.MaxValue;

        foreach ((string clusterId, int count) in overlapCounts)
        {
            // Only a still-registered cluster can be inherited; anything else no longer exists.
            if (!clusterRecords.TryGetValue(clusterId, out ClusterRecord record)) continue;

            // Most shared members wins; ties go to the cluster that has existed longer.
            if (count < bestCount) continue;
            if (count == bestCount && record.CreationSeq >= bestCreationSeq) continue;

            bestId = clusterId;
            bestCount = count;
            bestCreationSeq = record.CreationSeq;
        }

        Span<PassComponent> table = ComponentSpan;
        table[component].InheritedId = bestId;
        table[component].InheritedOverlap = bestCount;
    }

    /// <summary>
    ///     Settles two components inheriting the same ID and marks the surviving claim live for this
    ///     pass. The loser's inherited ID is cleared, which leaves it to be minted a fresh one.
    /// </summary>
    private void ResolveInheritanceConflict(int component)
    {
        Span<PassComponent> table = ComponentSpan;
        string? inherited = table[component].InheritedId;

        if (inherited is null) return;

        ClusterRecord record = clusterRecords[inherited];

        if (record.LastLivePass != passNumber)
        {
            ClaimClusterId(inherited, component);
            return;
        }

        // On an exact tie the component discovered first keeps the ID. Discovery order follows
        // grid enumeration, so which of two equal-sized fragments inherits is arbitrary — both
        // outcomes are equally correct, and archipelago made no guarantee here either.
        if (table[component].InheritedOverlap > table[record.Claimant].InheritedOverlap)
        {
            table[record.Claimant].InheritedId = null;
            ClaimClusterId(inherited, component);
        }
        else
            table[component].InheritedId = null;
    }

    /// <summary>
    ///     Records which component holds a cluster ID this pass, which also marks the cluster live.
    /// </summary>
    private void ClaimClusterId(string clusterId, int component)
    {
        ClusterRecord record = clusterRecords[clusterId];
        record.LastLivePass = passNumber;
        record.Claimant = component;
        clusterRecords[clusterId] = record;
    }

    private string MintClusterId()
    {
        var id = $"{options.IdPrefix}{++nextClusterNumber}";

        // Minted IDs are never inheritable in the pass that mints them — inheritance only reads
        // previous-pass assignments — so no component ever contests the claim.
        clusterRecords[id] = new ClusterRecord
        {
            CreationSeq = nextClusterNumber,
            LastLivePass = passNumber,
            Claimant = NONE,
        };

        return id;
    }

    /// <summary>
    ///     Drops bookkeeping for clusters that no longer exist, so the registry cannot grow without
    ///     bound over a long-lived process. Every component holds exactly one distinct ID, so
    ///     matching counts mean nothing vanished.
    /// </summary>
    private void PruneVanishedClusters()
    {
        if (clusterRecords.Count == components.Count) return;

        foreach ((string clusterId, ClusterRecord record) in clusterRecords)
            if (record.LastLivePass != passNumber)
                clusterRecords.Remove(clusterId);
    }

    /// <summary>
    ///     Materializes the immutable pass result: per-cluster geometry plus the per-peer detail the
    ///     stats surface serves.
    /// </summary>
    private ClusterPass BuildPass()
    {
        var clusterInfos = new ClusterInfo[components.Count];
        var peers = new ClusterPeerInfo[members.Count];
        var clusterIdByPeer = new string?[peerStates.Length];
        var peerCursor = 0;

        for (var component = 0; component < components.Count; component++)
            clusterInfos[component] = BuildCluster(component, peers, clusterIdByPeer, ref peerCursor);

        return new ClusterPass(clusterInfos, peers, clusterIdByPeer);
    }

    /// <summary>
    ///     Builds one cluster's metadata and appends its members to the pass-wide peer detail at
    ///     <paramref name="peerCursor" />. Centroid and radius are computed on the XZ plane,
    ///     matching what archipelago reported, so <c>engine.islands</c> stays comparable during
    ///     shadow mode.
    /// </summary>
    private ClusterInfo BuildCluster(
        int component,
        ClusterPeerInfo[] peers,
        string?[] clusterIdByPeer,
        ref int peerCursor)
    {
        PassComponent info = components[component];
        string realm = realmNames[info.RealmId];
        Vector3 centroid = Centroid(component, info.MemberCount);
        var radiusSquared = 0f;

        foreach (PassMember member in MembersOf(component))
        {
            float dx = member.Position.X - centroid.X;
            float dz = member.Position.Z - centroid.Z;

            radiusSquared = MathF.Max(radiusSquared, (dx * dx) + (dz * dz));

            peers[peerCursor++] = new ClusterPeerInfo(
                member.Peer, member.Wallet, info.Id, realm, member.Position, member.Parcel);

            clusterIdByPeer[member.Peer.Value] = info.Id;
        }

        return new ClusterInfo(info.Id, realm, info.MemberCount, centroid, MathF.Sqrt(radiusSquared));
    }

    private Vector3 Centroid(int component, int memberCount)
    {
        Vector3 sum = Vector3.Zero;

        foreach (PassMember member in MembersOf(component))
            sum += member.Position;

        return sum / memberCount;
    }

    /// <summary>
    ///     Records what this pass computed, so the next pass can measure cluster-identity overlap
    ///     against it. Kept separate from the published assignment: a fragment mid-debounce must
    ///     still inherit its own ID rather than be minted a new one every pass.
    /// </summary>
    private void RememberComputedAssignments()
    {
        for (var component = 0; component < components.Count; component++)
        {
            string clusterId = components[component].Id;

            foreach (PassMember member in MembersOf(component))
                peerStates[member.Peer.Value].PreviousPassClusterId = clusterId;
        }
    }

    /// <summary>
    ///     Publishes every peer whose assignment changed, subject to the dwell debounce, and returns
    ///     how many were published.
    /// </summary>
    private int PublishAssignmentChanges()
    {
        var reassignments = 0;

        for (var component = 0; component < components.Count; component++)
        {
            string clusterId = components[component].Id;

            foreach (PassMember member in MembersOf(component))
                if (TryPublishAssignment(member, clusterId))
                    reassignments++;
        }

        return reassignments;
    }

    /// <summary>
    ///     Emits a feed event for one peer if its published assignment — cluster and realm together
    ///     — differs from what the feed was last told, and either the change is exempt from the
    ///     debounce or the peer has dwelled long enough. Returns whether it published.
    /// </summary>
    private bool TryPublishAssignment(PassMember member, string clusterId)
    {
        ref PeerClusterState state = ref peerStates[member.Peer.Value];

        bool realmChanged = !string.Equals(state.PublishedRealm, member.Realm, StringComparison.Ordinal);

        if (!realmChanged && string.Equals(state.PublishedClusterId, clusterId, StringComparison.Ordinal))
        {
            state.CandidateClusterId = null;
            state.CandidateStreak = 0;

            return false;
        }

        // The debounce is bypassed on first assignment, teleport, realm change, and deletion of the
        // peer's previous cluster — cases where the published assignment is already known to be
        // wrong, so waiting would keep serving a stale room.
        bool immediate = state.PublishedClusterId is null
                         || member.IsTeleport
                         || realmChanged
                         || !IsClusterLive(state.PublishedClusterId);

        if (!immediate && !HasDwelled(ref state, clusterId)) return false;

        state.PublishedClusterId = clusterId;
        state.PublishedRealm = member.Realm;
        state.CandidateClusterId = null;
        state.CandidateStreak = 0;

        feedPublisher.PublishClusterChange(member.Wallet, clusterId, member.Realm);

        return true;
    }

    private bool IsClusterLive(string clusterId) =>
        clusterRecords.TryGetValue(clusterId, out ClusterRecord record) && record.LastLivePass == passNumber;

    /// <summary>
    ///     Advances the peer's candidate streak and reports whether the new assignment has now
    ///     been agreed on by <see cref="ClusterOptions.DwellPasses" /> consecutive passes.
    /// </summary>
    private bool HasDwelled(ref PeerClusterState state, string clusterId)
    {
        if (string.Equals(state.CandidateClusterId, clusterId, StringComparison.Ordinal))
            state.CandidateStreak++;
        else
        {
            state.CandidateClusterId = clusterId;
            state.CandidateStreak = 1;
        }

        return state.CandidateStreak >= options.DwellPasses;
    }

    /// <summary>
    ///     Clears carried-over state for peers absent from this pass. Mandatory rather than tidy:
    ///     <see cref="PeerIndex" /> is a recycled ENet slot, so state left behind for a departed
    ///     peer would otherwise be inherited by whichever wallet lands on that slot next and make
    ///     its first assignment look like an unchanged one.
    /// </summary>
    private void ForgetVanishedPeers()
    {
        for (var peerSlot = 0; peerSlot < peerStates.Length; peerSlot++)
        {
            ref PeerClusterState state = ref peerStates[peerSlot];

            // An unstamped slot is already clear — never collected, or forgotten by an earlier pass.
            if (state.LastSeenPass == passNumber || state.LastSeenPass == 0) continue;

            state = default(PeerClusterState);
        }
    }

    private MemberEnumerator MembersOf(int component) =>
        new (this, components[component].FirstNode);

    /// <summary>
    ///     A union-find node: one grid cell within one realm. Keying by realm is what keeps a
    ///     cluster from ever spanning realms.
    /// </summary>
    private readonly record struct NodeKey(int RealmId, long CellKey)
    {
        /// <summary>
        ///     The cell coordinates are mixed as two separate inputs, deliberately.
        ///     <see cref="long" /> hashes as <c>low ^ high</c>, so a packed cell key folds to
        ///     <c>x ^ z</c>: a 96x96 grid of cells yields 128 distinct hash codes, and every cell on
        ///     an anti-diagonal collides. Feeding <see cref="CellKey" /> whole — to the generated
        ///     record hash or to <c>HashCode.Combine(RealmId, CellKey)</c> — does not fix it, because
        ///     the fold happens before any mixing and no mixing restores lost entropy. Probing the
        ///     node table is the bulk of a pass, so those chains would dominate its cost.
        /// </summary>
        public override int GetHashCode() =>
            HashCode.Combine(RealmId, (int)CellKey, (int)(CellKey >> 32));
    }

    /// <summary>
    ///     A peer as observed by one pass, resolved once so later steps never re-read the boards
    ///     and never see a torn view of a peer mid-pass.
    /// </summary>
    private readonly record struct PassMember(
        PeerIndex Peer,
        string Wallet,
        string Realm,
        Vector3 Position,
        int Parcel,
        bool IsTeleport
    );

    /// <summary>
    ///     One node of the cell graph: its identity, the slice of <see cref="members" /> it owns,
    ///     its union-find links, and the component it ended up in. Mutable, and updated in place
    ///     through <see cref="NodeSpan" /> — union-find rewrites parents on nearly every read.
    /// </summary>
    private struct PassNode
    {
        public NodeKey Key;
        public int MemberStart;
        public int MemberCount;

        public int Parent;
        public int TreeSize;

        // Meaningful on a root node only; NONE until GroupComponents reaches it.
        public int Component;

        // Next node of the same component, or NONE at the end of the chain.
        public int NextInComponent;
    }

    /// <summary>
    ///     One connected component of the cell graph, and the cluster it publishes as. Mutable, and
    ///     updated in place through <see cref="ComponentSpan" />.
    /// </summary>
    private struct PassComponent
    {
        public int RealmId;

        // Head of this component's node chain, or NONE while it is still empty.
        public int FirstNode;
        public int MemberCount;

        // Sticky-ID working state, live only across the steps of AssignStickyIds.
        public string? InheritedId;
        public int InheritedOverlap;

        // Empty until AssignStickyIds settles it.
        public string Id;
    }

    /// <summary>
    ///     What the tracker carries about one peer slot between passes.
    ///     <para />
    ///     Two distinct notions of "previous cluster", deliberately kept apart:
    ///     <see cref="PreviousPassClusterId" /> is what the last pass <i>computed</i>. It defines
    ///     cluster identity continuity, so it is what sticky-ID inheritance measures overlap
    ///     against. <see cref="PublishedClusterId" /> is what consumers were last <i>told</i>, which
    ///     the dwell debounce holds back — only the debounce decision may read it.
    ///     <para />
    ///     Conflating them starves the debounce: a fragment whose reassignment is still being
    ///     debounced would look unassigned to the inheritance step and be minted a fresh ID on every
    ///     pass, so its candidate would never repeat and its streak would never reach DwellPasses.
    /// </summary>
    private struct PeerClusterState
    {
        public string? PreviousPassClusterId;

        // Cluster and realm as last published together — the feed carries both, so either one
        // changing is a change.
        public string? PublishedClusterId;
        public string? PublishedRealm;

        public string? CandidateClusterId;
        public int CandidateStreak;

        // Pass this slot was last collected in. Zero means never, or forgotten since.
        public long LastSeenPass;
    }

    /// <summary>
    ///     Bookkeeping for one cluster ID that exists or existed. <see cref="LastLivePass" /> equal
    ///     to the current pass marks the cluster live, and is what makes <see cref="Claimant" />
    ///     meaningful; anything older is pruned at the end of the pass.
    /// </summary>
    private struct ClusterRecord
    {
        // Mint order. Lower means older, which wins inheritance ties.
        public long CreationSeq;
        public long LastLivePass;
        public int Claimant;
    }

    /// <summary>
    ///     Walks one component's members: its chain of nodes, and within each node the contiguous
    ///     slice of <see cref="members" /> that node owns. Mirrors the shape of
    ///     <see cref="SpatialGrid.OccupiedCellEnumerator" /> so both read the same way at the call
    ///     site, and keeps the two-level indirection out of every step that needs members.
    /// </summary>
    private struct MemberEnumerator(ClusterTracker tracker, int firstNode)
    {
        private int nodeCursor = firstNode;
        private int memberCursor;
        private int memberEnd;

        public PassMember Current { get; private set; }

        public bool MoveNext()
        {
            // Every node holds at least one member, so this advances at most one node per call.
            while (memberCursor == memberEnd)
            {
                if (nodeCursor == NONE) return false;

                PassNode node = tracker.nodes[nodeCursor];

                memberCursor = node.MemberStart;
                memberEnd = node.MemberStart + node.MemberCount;
                nodeCursor = node.NextInComponent;
            }

            Current = tracker.members[memberCursor++];

            return true;
        }

        public MemberEnumerator GetEnumerator() =>
            this;
    }
}
