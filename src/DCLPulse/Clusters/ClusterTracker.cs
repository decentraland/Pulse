using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Pulse.Clusters;

/// <summary>
///     Derives cluster membership on its own thread, off the hot path: one pass every
///     <see cref="ClusterOptions.PassIntervalMs" /> reads <see cref="RealmSpatialGrids" /> and
///     <see cref="SnapshotBoard" /> and touches no worker, peer state dict or <c>PeerSimulation</c>.
///     <para />
///     A pass runs weighted union-find with path halving over occupied grid cells using 8-neighbor
///     adjacency, one realm's grid at a time. A cluster cannot span realms because the cells it is
///     built from cannot: realm isolation is the grid's, so partitioning costs no realm comparison
///     here. (<see cref="TryPublishAssignment" /> still compares the published realm — that is change
///     detection for the feed, not partitioning.) Cost is O(N + C) in peers and occupied cells — no
///     peer-pair tests. Working buffers are fields, cleared rather than reallocated between passes.
/// </summary>
public sealed class ClusterTracker : BackgroundService
{
    // Absent link — no component, no next node, no claimant.
    private const int NONE = -1;

    // Forward half of the 8-neighborhood: the +X column plus the cell straight ahead in +Z. Every
    // node probes and union is symmetric, so each adjacent pair is still visited exactly once — half
    // the lookups of the full ring for an identical partition.
    private static readonly int[] NEIGHBOR_DX = [1, 1, 1, 0];
    private static readonly int[] NEIGHBOR_DZ = [-1, 0, 1, 1];

    private readonly ILogger<ClusterTracker> logger;
    private readonly ClusterOptions options;
    private readonly RealmSpatialGrids realmGrids;
    private readonly SnapshotBoard snapshotBoard;
    private readonly IdentityBoard identityBoard;
    private readonly ClusterBoard clusterBoard;
    private readonly IClusterFeedPublisher feedPublisher;

    // Cell graph for the realm being collected. One node per cell, carrying its slice of members and
    // its own union-find state. Cleared between realms — the same cell exists in every realm, and
    // only same-realm neighbors may union.
    private readonly Dictionary<NodeKey, int> nodeIndexByKey = new ();
    private readonly List<PassNode> nodes = [];

    // Per-pass members, ordered so every node's members are one contiguous slice.
    private readonly List<PassMember> members = [];

    // Per-pass components, each a chain of the nodes that union-find merged into it.
    private readonly List<PassComponent> components = [];

    // Scratch for one component's overlap tally against the previous pass. Cleared per component,
    // and holds one entry per previous cluster the component draws members from.
    private readonly Dictionary<string, int> overlapCounts = new ();

    // State carried across passes, owned solely by this thread.
    private readonly PeerClusterState[] peerStates;
    private readonly Dictionary<string, ClusterRecord> clusterRecords = new ();

    private long passNumber;
    private long nextClusterNumber;

    // Last value published for each gauge. An up-down counter takes a delta, not an absolute.
    private int lastClusterCount;
    private int lastClusterPeers;
    private int lastSizeMax;

    public ClusterTracker(
        ILogger<ClusterTracker> logger,
        IOptions<ClusterOptions> options,
        RealmSpatialGrids realmGrids,
        SnapshotBoard snapshotBoard,
        IdentityBoard identityBoard,
        ClusterBoard clusterBoard,
        IClusterFeedPublisher feedPublisher,
        int maxPeers)
    {
        this.logger = logger;
        this.options = options.Value;
        this.realmGrids = realmGrids;
        this.snapshotBoard = snapshotBoard;
        this.identityBoard = identityBoard;
        this.clusterBoard = clusterBoard;
        this.feedPublisher = feedPublisher;

        peerStates = new PeerClusterState[maxPeers];
    }

    // A List indexer hands back a copy of a struct element, so in-place updates go through the
    // backing store instead. Properties rather than cached locals: a re-read cannot go stale when
    // the list grows mid-loop.
    private Span<PassNode> NodeSpan =>
        CollectionsMarshal.AsSpan(nodes);

    private Span<PassComponent> ComponentSpan =>
        CollectionsMarshal.AsSpan(components);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            // Warning, not Information: production ships Logging:LogLevel:Default = Warning, so an
            // Information line never reaches the deployment log. A tracker that is not running is
            // exactly what an operator needs to see there.
            logger.LogWarning("Cluster tracker disabled (Clusters:Enabled is false)");
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
    ///     One clustering pass, start to finish. Internal rather than private so a pass can be driven
    ///     directly instead of by the background loop's timer.
    /// </summary>
    internal void RunPass()
    {
        long startTicks = Stopwatch.GetTimestamp();

        // Stamps every per-pass liveness check: a peer slot, a cluster record and a claim are
        // current exactly when they carry this number, so nothing has to be cleared to go stale.
        passNumber++;

        CollectAndUnionRealms();
        GroupComponents();
        AssignStickyIds();

        ClusterPass pass = BuildPass();

        RememberComputedAssignments();
        clusterBoard.Publish(pass);

        // Topology before the per-peer events, so a snapshot declaring a cluster is published ahead
        // of the assignments that reference it.
        feedPublisher.PublishTopology(pass);

        int reassignments = PublishAssignmentChanges();
        ForgetVanishedPeers();

        RecordPassMetrics(startTicks, pass.Clusters.Count, reassignments);
    }

    private void RecordPassMetrics(long startTicks, int clusterCount, int reassignments)
    {
        PulseMetrics.Clusters.PASSES.Add(1);
        PulseMetrics.Clusters.PASS_DURATION_US.Add((long)Stopwatch.GetElapsedTime(startTicks).TotalMicroseconds);

        if (reassignments > 0)
            PulseMetrics.Clusters.REASSIGNMENTS.Add(reassignments);

        RecordClusterSizes(out int peers, out int largest);

        RecordGauge(PulseMetrics.Clusters.COUNT, clusterCount, ref lastClusterCount);
        RecordGauge(PulseMetrics.Clusters.PEERS, peers, ref lastClusterPeers);
        RecordGauge(PulseMetrics.Clusters.SIZE_MAX, largest, ref lastSizeMax);
    }

    /// <summary>
    ///     Records one histogram observation per cluster and returns the two totals the histogram cannot
    ///     answer: how many peers were clustered at all, and the largest cluster. Reads
    ///     <see cref="PassComponent.MemberCount" /> rather than the built <see cref="ClusterPass" />, so
    ///     it is independent of how the pass is materialized.
    /// </summary>
    private void RecordClusterSizes(out int peers, out int largest)
    {
        peers = 0;
        largest = 0;

        for (var component = 0; component < components.Count; component++)
        {
            int size = components[component].MemberCount;

            PulseMetrics.Clusters.SIZE.Record(size);

            peers += size;
            largest = Math.Max(largest, size);
        }
    }

    /// <summary>
    ///     Publishes an absolute gauge value through an up-down counter, which takes a delta.
    /// </summary>
    private static void RecordGauge(UpDownCounter<int> gauge, int value, ref int previous)
    {
        gauge.Add(value - previous);
        previous = value;
    }

    /// <summary>
    ///     Builds and unions the cell graph, realm by realm. Nodes and members accumulate across
    ///     realms — later steps work on the pass as a whole — while the key index that neighbor probes
    ///     consult holds one realm at a time, which is what confines every union to a single realm.
    /// </summary>
    private void CollectAndUnionRealms()
    {
        nodes.Clear();
        members.Clear();

        foreach (RealmSpatialGrids.RealmGrid realmGrid in realmGrids.GetRealmGrids())
        {
            nodeIndexByKey.Clear();

            int realmFirstNode = nodes.Count;

            CollectRealmNodes(realmGrid.Realm, realmGrid.Grid);
            UnionRealmNeighbors(realmFirstNode);
        }
    }

    /// <summary>
    ///     Reads one realm's occupied cells and builds a node per cell that has at least one collectable
    ///     occupant. Every occupant of the grid is in this realm, so no member needs a realm test.
    /// </summary>
    private void CollectRealmNodes(string realm, SpatialGrid grid)
    {
        foreach (SpatialGrid.OccupiedCell cell in grid.GetOccupiedCells())
        {
            int firstMember = members.Count;

            foreach (PeerIndex peer in cell.Occupants)
                TryCollectMember(peer);

            // Later steps assume every node owns at least one member.
            if (members.Count == firstMember) continue;

            AddNode(realm, cell.Key, firstMember, members.Count - firstMember);
        }
    }

    /// <summary>
    ///     Resolves one occupant into a <see cref="PassMember" />, or skips it. A peer whose snapshot is
    ///     unreadable or whose wallet is unknown cannot be published as a cluster member. A peer already
    ///     collected this pass is skipped too: the grid read is weakly consistent, so a peer that
    ///     changes cell — or realm — mid-enumeration can surface twice, and every later step assumes a
    ///     peer appears at most once. The grid it was found in first decides the realm it clusters in.
    /// </summary>
    private void TryCollectMember(PeerIndex peer)
    {
        ref PeerClusterState state = ref peerStates[peer.Value];

        if (state.LastSeenPass == passNumber) return;
        if (!snapshotBoard.TryRead(peer, out PeerSnapshot snapshot)) return;

        string? wallet = identityBoard.GetWalletIdByPeerIndex(peer);

        if (wallet is null) return;

        state.LastSeenPass = passNumber;

        members.Add(new PassMember(peer, wallet, snapshot.GlobalPosition, snapshot.Parcel, snapshot.IsTeleport));
    }

    private void AddNode(string realm, long cellKey, int memberStart, int memberCount)
    {
        int index = nodes.Count;

        nodeIndexByKey[new NodeKey(cellKey)] = index;

        nodes.Add(new PassNode
        {
            Realm = realm,
            CellKey = cellKey,
            MemberStart = memberStart,
            MemberCount = memberCount,
            Parent = index,
            TreeSize = 1,
            Component = NONE,
            NextInComponent = NONE,
        });
    }

    /// <summary>
    ///     Unions each of one realm's nodes with its neighbors. Cell adjacency is the whole join test:
    ///     two peers in adjacent cells are between 0 and <c>2 * CellSize * sqrt(2)</c> apart, and the
    ///     resulting boundary noise is absorbed by the dwell debounce rather than by a second
    ///     distance threshold.
    /// </summary>
    private void UnionRealmNeighbors(int firstNode)
    {
        Span<PassNode> table = NodeSpan;

        for (int node = firstNode; node < table.Length; node++)
        {
            SpatialGrid.UnpackKey(table[node].CellKey, out int x, out int z);

            for (var i = 0; i < NEIGHBOR_DX.Length; i++)
            {
                var neighborKey = new NodeKey(SpatialGrid.PackKey(x + NEIGHBOR_DX[i], z + NEIGHBOR_DZ[i]));

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
    ///     time the root is reached and pushing the node onto its chain. The chain plus each node's
    ///     contiguous member slice is what lets a component's members be walked without a separate
    ///     ordering array. Only a root node's <see cref="PassNode.Component" /> is meaningful.
    /// </summary>
    private void AttachToComponent(Span<PassNode> table, int node)
    {
        int root = Find(table, node);
        int component = table[root].Component;

        if (component == NONE)
        {
            component = components.Count;

            // Union only ever merges nodes of one realm's grid, so any node names the component's realm.
            components.Add(new PassComponent
            {
                Realm = table[node].Realm,
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
    ///     Gives each component the ID of the previous cluster it shares the most members with, so a
    ///     crowd that splits or merges keeps a stable identity across passes. Ties resolve to the
    ///     older cluster; when two components claim the same ID the larger overlap keeps it and the
    ///     other takes a fresh one.
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

        // On an exact tie the component discovered first keeps the ID. Discovery order follows grid
        // enumeration, so which of two equal-sized fragments inherits is arbitrary.
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

        // Inheritance only reads previous-pass assignments, so a freshly minted ID is uncontested.
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
    ///     bound. Every component holds exactly one distinct ID, so matching counts mean nothing
    ///     vanished.
    /// </summary>
    private void PruneVanishedClusters()
    {
        if (clusterRecords.Count == components.Count) return;

        // Removing during enumeration is deliberate and supported: since .NET Core 3.0
        // Dictionary.Remove does not invalidate an active enumerator, so this needs no second pass
        // and no key list. Adding still would invalidate it — only removal is exempt.
        foreach ((string clusterId, ClusterRecord record) in clusterRecords)
            if (record.LastLivePass != passNumber)
                clusterRecords.Remove(clusterId);
    }

    /// <summary>
    ///     Materializes the immutable pass result: per-cluster geometry plus per-peer detail.
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
    ///     <paramref name="peerCursor" />. Centroid and radius are computed on the XZ plane only.
    /// </summary>
    private ClusterInfo BuildCluster(
        int component,
        ClusterPeerInfo[] peers,
        string?[] clusterIdByPeer,
        ref int peerCursor)
    {
        PassComponent info = components[component];
        Vector3 centroid = Centroid(component, info.MemberCount);
        var radiusSquared = 0f;

        foreach (PassMember member in MembersOf(component))
        {
            float dx = member.Position.X - centroid.X;
            float dz = member.Position.Z - centroid.Z;

            radiusSquared = MathF.Max(radiusSquared, (dx * dx) + (dz * dz));

            peers[peerCursor++] = new ClusterPeerInfo(
                member.Peer, member.Wallet, info.Id, info.Realm, member.Position, member.Parcel);

            clusterIdByPeer[member.Peer.Value] = info.Id;
        }

        return new ClusterInfo(info.Id, info.Realm, info.MemberCount, centroid, MathF.Sqrt(radiusSquared));
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
    ///     against it. Kept separate from the published assignment: a fragment mid-debounce must still
    ///     inherit its own ID rather than be minted a new one every pass.
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
            PassComponent info = components[component];

            foreach (PassMember member in MembersOf(component))
                if (TryPublishAssignment(member, info.Id, info.Realm))
                    reassignments++;
        }

        return reassignments;
    }

    /// <summary>
    ///     Emits a feed event for one peer if its assignment — cluster and realm together — differs
    ///     from the last one published, and either the change is exempt from the debounce or the peer
    ///     has dwelled long enough. Returns whether it published.
    /// </summary>
    private bool TryPublishAssignment(PassMember member, string clusterId, string realm)
    {
        ref PeerClusterState state = ref peerStates[member.Peer.Value];

        bool realmChanged = !string.Equals(state.PublishedRealm, realm, StringComparison.Ordinal);

        if (!realmChanged && string.Equals(state.PublishedClusterId, clusterId, StringComparison.Ordinal))
        {
            state.CandidateClusterId = null;
            state.CandidateStreak = 0;

            return false;
        }

        // The debounce is bypassed on first assignment, teleport, realm change, and deletion of the
        // peer's previous cluster — cases where the published assignment is already known to be
        // wrong, so waiting would only prolong it.
        bool immediate = state.PublishedClusterId is null
                         || member.IsTeleport
                         || realmChanged
                         || !IsClusterLive(state.PublishedClusterId);

        if (!immediate && !HasDwelled(ref state, clusterId)) return false;

        state.PublishedClusterId = clusterId;
        state.PublishedRealm = realm;
        state.CandidateClusterId = null;
        state.CandidateStreak = 0;

        feedPublisher.PublishClusterChange(member.Wallet, clusterId, realm);

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
    ///     <see cref="PeerIndex" /> is a recycled ENet slot, so state left behind would be inherited
    ///     by the next wallet on that slot and make its first assignment look unchanged.
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
    ///     A union-find node's identity within the realm currently being collected: its grid cell.
    ///     A wrapper rather than a bare <see cref="long" /> key purely for the hash below.
    /// </summary>
    private readonly record struct NodeKey(long CellKey)
    {
        /// <summary>
        ///     The cell coordinates are mixed as two separate inputs, deliberately.
        ///     <see cref="long" /> hashes as <c>low ^ high</c>, so a packed cell key folds to
        ///     <c>x ^ z</c>: a 96x96 grid of cells yields 128 distinct hash codes, and every cell on
        ///     an anti-diagonal collides. Feeding <see cref="CellKey" /> whole does not fix it — the
        ///     fold happens before any mixing, and no mixing restores lost entropy.
        /// </summary>
        public override int GetHashCode() =>
            HashCode.Combine((int)CellKey, (int)(CellKey >> 32));
    }

    /// <summary>
    ///     A peer as observed by one pass, resolved once so later steps never re-read the boards and
    ///     never see a torn view of a peer mid-pass. Carries no realm: the realm belongs to the node
    ///     the member was collected into, and every member of a node shares it.
    /// </summary>
    private readonly record struct PassMember(
        PeerIndex Peer,
        string Wallet,
        Vector3 Position,
        int Parcel,
        bool IsTeleport
    );

    /// <summary>
    ///     One node of the cell graph: its identity, the slice of <see cref="members" /> it owns, its
    ///     union-find links, and the component it ended up in. Updated in place through
    ///     <see cref="NodeSpan" /> — union-find rewrites parents on nearly every read.
    /// </summary>
    private struct PassNode
    {
        public string Realm;
        public long CellKey;
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
    ///     One connected component of the cell graph, and the cluster it publishes as. Updated in
    ///     place through <see cref="ComponentSpan" />.
    /// </summary>
    private struct PassComponent
    {
        public string Realm;

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
    ///     Two notions of "previous cluster", deliberately kept apart:
    ///     <see cref="PreviousPassClusterId" /> is what the last pass <i>computed</i>, which is what
    ///     sticky-ID inheritance measures overlap against; <see cref="PublishedClusterId" /> is what
    ///     the feed was last <i>told</i>, read only by the debounce decision. Conflating them starves
    ///     the debounce: a fragment mid-debounce would look unassigned to inheritance and be minted a
    ///     fresh ID every pass, so its candidate would never repeat and its streak never reach
    ///     <see cref="ClusterOptions.DwellPasses" />.
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
    ///     Bookkeeping for one cluster ID that exists or existed. <see cref="LastLivePass" /> equal to
    ///     the current pass marks the cluster live and makes <see cref="Claimant" /> meaningful;
    ///     anything older is pruned at the end of the pass.
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
    ///     slice of <see cref="members" /> that node owns, keeping the two-level indirection out of
    ///     every step that needs members.
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
