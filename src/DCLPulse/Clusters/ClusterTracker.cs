using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    private readonly ParcelEncoder parcelEncoder;
    private readonly ITimeProvider timeProvider;

    // Whether engine.parcel_changes is derived at all. No broker means nothing to publish to.
    private readonly bool presenceEnabled;

    // Guards the presence columns of peerStates alone. The pass writes them on this thread and
    // OnPeerRemoved on a peer worker; the clustering columns are this thread's only and take no lock.
    private readonly Lock presenceLock = new ();

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

    // State carried across passes. The clustering columns are this thread's alone; the presence
    // columns are shared with OnPeerRemoved under presenceLock.
    private readonly PeerState[] peerStates;
    private readonly Dictionary<string, ClusterRecord> clusterRecords = new ();

    // Last assignment published for each wallet. Keyed by wallet rather than PeerIndex so an
    // entry outlives the slot it was written from.
    private readonly Dictionary<string, WalletAssignment> assignmentByWallet = new (StringComparer.OrdinalIgnoreCase);

    private long passNumber;
    private long nextClusterNumber;

    // Peers holding a published presence, so a snapshot's list is sized once instead of grown.
    private int liveCount;

    // Order presence occupancies were acquired in, which breaks a tie between two slots of one
    // wallet. ulong at one pass per second, so it cannot wrap.
    private ulong occupancyStamp;

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
        ParcelEncoder parcelEncoder,
        IOptions<NatsOptions> natsOptions,
        ITimeProvider timeProvider,
        int maxPeers)
    {
        this.logger = logger;
        this.options = options.Value;
        this.realmGrids = realmGrids;
        this.snapshotBoard = snapshotBoard;
        this.identityBoard = identityBoard;
        this.clusterBoard = clusterBoard;
        this.feedPublisher = feedPublisher;
        this.parcelEncoder = parcelEncoder;
        this.timeProvider = timeProvider;

        presenceEnabled = natsOptions.Value.IsConfigured;

        peerStates = new PeerState[maxPeers];
    }

    /// <summary>
    ///     Whether <c>engine.parcel_changes</c> is derived. False leaves every presence entry point a
    ///     no-op; clustering runs regardless.
    /// </summary>
    public bool PresenceEnabled => presenceEnabled;

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

        clusterBoard.Publish(pass);

        // Topology before the per-peer events, so a snapshot declaring a cluster is published ahead
        // of the assignments that reference it.
        feedPublisher.PublishTopology(pass);

        int reassignments = PublishPeerChanges();
        ForgetVanishedPeers();
        ForgetExpiredSessions();

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
    ///     unreadable, whose wallet is unknown, or which is no longer the wallet's live binding in
    ///     <see cref="IdentityBoard" /> cannot be published as a cluster member.
    ///     A binding goes stale when a duplicate-session eviction rebinds the wallet to a replacement
    ///     peer while the outgoing one is still in the grid awaiting its transport disconnect.
    ///     A peer already collected this pass is skipped too: the grid read is weakly consistent, so a peer
    ///     that changes cell — or realm — mid-enumeration can surface twice, and every later step assumes
    ///     a peer appears at most once. The grid it was found in first decides the realm it clusters in.
    /// </summary>
    private void TryCollectMember(PeerIndex peer)
    {
        ref PeerState state = ref peerStates[peer.Value];

        if (state.LastSeenPass == passNumber) return;
        if (!snapshotBoard.TryRead(peer, out PeerSnapshot snapshot)) return;

        string? wallet = identityBoard.GetWalletIdByPeerIndex(peer);

        if (wallet is null) return;

        // Only the wallet's current live binding is collected; a peer holding a stale one is skipped.
        // Outside that case the reverse lookup resolves back to this same peer, so the check is a
        // no-op — one dictionary read per occupant, on the tracker's own 1 Hz thread.
        if (!identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex live) || live != peer) return;

        state.LastSeenPass = passNumber;

        // Refreshing only on publish would expire the entry under a stationary peer, which publishes
        // once and never again.
        ref WalletAssignment seen = ref CollectionsMarshal.GetValueRefOrNullRef(assignmentByWallet, wallet);

        if (!Unsafe.IsNullRef(ref seen))
            seen.LastSeenPass = passNumber;

        members.Add(new PassMember(peer, wallet, identityBoard.GetSessionByPeerIndex(peer) ?? wallet,
            snapshot.GlobalPosition, snapshot.Parcel, snapshot.IsTeleport));
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

        return new ClusterPass(clusterInfos, peers, clusterIdByPeer, timeProvider.UnixTimeMs);
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

            // What this pass computed, which is what the next pass measures cluster-identity overlap
            // against. Kept apart from PublishedClusterId: a fragment mid-debounce must still inherit
            // its own ID rather than be minted a new one every pass.
            peerStates[member.Peer.Value].PreviousPassClusterId = info.Id;
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
    ///     The pass's one per-peer publish walk: each member's parcel change and its cluster
    ///     assignment, in that order. Returns how many assignments were published.
    ///     <para />
    ///     Both feeds come off the same walk of the same members, so what
    ///     <c>engine.parcel_changes</c> says and what the stats surface serves cannot disagree by more
    ///     than one pass. The presence half runs under <see cref="presenceLock" />, which is also what
    ///     makes "read the slot, publish, write the slot" atomic against <see cref="OnPeerRemoved" />.
    /// </summary>
    private int PublishPeerChanges()
    {
        var reassignments = 0;

        // Asked before the walk, because a snapshot sends every live peer and the per-peer deltas
        // would then be redundant. Slots are brought up to date either way.
        var reason = default(PresenceSnapshotReason);
        bool snapshot = presenceEnabled && feedPublisher.TryTakeParcelSnapshotRequest(out reason);

        for (var component = 0; component < components.Count; component++)
        {
            PassComponent info = components[component];

            foreach (PassMember member in MembersOf(component))
            {
                // Taken per member rather than around the whole walk: OnPeerRemoved runs on a peer
                // worker inside the simulation tick, so it must not queue behind the assignment half
                // as well. Uncontended acquisitions, at one pass per second.
                if (presenceEnabled)
                    lock (presenceLock)
                        ObservePresence(member, info.Realm, publishChange: !snapshot);

                if (TryPublishAssignment(member, info.Id, info.Realm))
                    reassignments++;
            }
        }

        if (!presenceEnabled) return reassignments;

        // An outbox eviction is raised by the deltas just published, so its snapshot request does not
        // exist until the walk above has run. Answering it here rather than next pass is what makes
        // C1.4's "immediately after any outbox eviction" the next batch turn.
        if (!snapshot && feedPublisher.TryTakeParcelSnapshotRequest(out PresenceSnapshotReason raisedByThisPass))
        {
            snapshot = true;
            reason = raisedByThisPass;
        }

        if (snapshot)
            lock (presenceLock)
                feedPublisher.PublishParcelSnapshot(CollectLivePresence(), reason);

        return reassignments;
    }

    /// <summary>
    ///     Emits a feed event for one peer if its assignment — cluster and realm together — differs
    ///     from the last one published, and either the change is exempt from the debounce or the peer
    ///     has dwelled long enough. Returns whether it published.
    /// </summary>
    private bool TryPublishAssignment(PassMember member, string clusterId, string realm)
    {
        ref PeerState state = ref peerStates[member.Peer.Value];

        bool realmChanged = !string.Equals(state.PublishedRealm, realm, StringComparison.Ordinal);

        if (!realmChanged && string.Equals(state.PublishedClusterId, clusterId, StringComparison.Ordinal))
        {
            state.CandidateClusterId = null;
            state.CandidateStreak = 0;

            return false;
        }

        // The debounce is bypassed on first assignment, teleport, realm change and deletion of the
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

        // Read before the ledger is overwritten below: the previous entry is what names a takeover.
        ClusterSession session = SessionFor(member);

        assignmentByWallet[member.Wallet] = new WalletAssignment
        {
            ClusterId = clusterId,
            Realm = realm,
            Session = member.Session,
            LastSeenPass = passNumber,
        };

        feedPublisher.PublishClusterChange(member.Wallet, clusterId, realm, session);

        if (session.DisplacedSession is not null)
            PulseMetrics.Clusters.TAKEOVERS.Add(1);

        return true;
    }

    /// <summary>
    ///     Names the session this publish belongs to and, when the wallet's retained assignment was
    ///     published by a different session, that session and the cluster it was last published into.
    ///     A same-session reconnect names nothing: only the session key tells one device's return
    ///     from another device's arrival. Deliberately not gated on the displaced cluster still
    ///     existing — a peer that was alone took its cluster with it, while the LiveKit room it was in
    ///     outlives it.
    /// </summary>
    private ClusterSession SessionFor(PassMember member)
    {
        if (options.SessionRetentionPasses > 0
            && assignmentByWallet.TryGetValue(member.Wallet, out WalletAssignment retained)
            && !string.Equals(retained.Session, member.Session, StringComparison.OrdinalIgnoreCase))
            return new ClusterSession(member.Session, retained.Session, retained.ClusterId);

        return new ClusterSession(member.Session, null, null);
    }

    private bool IsClusterLive(string clusterId) =>
        clusterRecords.TryGetValue(clusterId, out ClusterRecord record) && record.LastLivePass == passNumber;

    /// <summary>
    ///     Advances the peer's candidate streak and reports whether the new assignment has now
    ///     been agreed on by <see cref="ClusterOptions.DwellPasses" /> consecutive passes.
    /// </summary>
    private bool HasDwelled(ref PeerState state, string clusterId)
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
    ///     Brings one member's presence columns up to date and, when <paramref name="publishChange" />
    ///     is set, publishes an entry if its <c>(realm, parcel)</c> moved. A member with no presence
    ///     yet has changed by definition: its previous state is "nowhere" (C1.1).
    /// </summary>
    private void ObservePresence(PassMember member, string realm, bool publishChange)
    {
        ref PeerState state = ref peerStates[member.Peer.Value];

        parcelEncoder.Decode(member.Parcel, out int x, out int z);

        var parcel = new ParcelCoord(x, z);

        // A different wallet on this slot is a new presence wherever it stands — guarding the same
        // aliasing class as PeerSimulation.DetectAndHandleAliasing. Reference equality is the fast
        // path: IdentityBoard hands out one string instance per peer for as long as it lives.
        bool sameWallet = ReferenceEquals(state.Wallet, member.Wallet)
                       || string.Equals(state.Wallet, member.Wallet, StringComparison.OrdinalIgnoreCase);

        if (state.Realm is not null
            && sameWallet
            && state.Parcel == parcel
            && string.Equals(state.Realm, realm, StringComparison.Ordinal))
            return;

        // Read before state.Realm is written below, since that is what marks an empty slot.
        bool acquired = state.Realm is null || !sameWallet;

        if (state.Realm is null)
            liveCount++;

        state.Wallet = member.Wallet;

        // Lowercased once per wallet rather than once per published entry.
        if (!sameWallet)
            state.Address = CanonicalName.Of(member.Wallet);

        state.Realm = realm;
        state.Parcel = parcel;

        // On acquisition only, never on a later move, so the tie-break is recency of the *session*.
        // A kicked connection keeps moving for a client round trip, so a stamp rewritten on every
        // change would be the stale slot's whenever it moved last — and which moved last is decided
        // by grid.GetOccupiedCells() order, which is spatial. Occupancy order is monotone in session
        // age; placement order is not.
        if (acquired)
            state.OccupiedAt = ++occupancyStamp;

        if (publishChange)
            feedPublisher.PublishParcelChange(state.Address!, realm, parcel);
    }

    /// <summary>
    ///     The single presence exit choke point (C1.2): one <c>parcel</c>-absent entry for a peer that
    ///     had a published presence, nothing for one that never did. Called from a peer worker.
    ///     <para />
    ///     Wallet-scoped (A1) — the entry goes out only once no live peer is bound to the wallet. A
    ///     duplicate-session kick rebinds the wallet to the incoming peer long before this runs, so
    ///     publishing here would take a peer offline that is online on its newer connection; and with
    ///     both in one batch the per-address coalescing keeps the exit and drops the placement.
    ///     <para />
    ///     Clearing the columns is mandatory, not tidy: <see cref="PeerIndex" /> is recycled, and
    ///     leftover state would make the next wallet's first placement look unchanged.
    /// </summary>
    public void OnPeerRemoved(PeerIndex peer)
    {
        if (!presenceEnabled) return;

        var index = (int)peer.Value;

        if (index >= peerStates.Length) return;

        lock (presenceLock)
        {
            ref PeerState state = ref peerStates[index];

            if (state.Realm is not { } realm)
            {
                ClearPresence(ref state);

                return;
            }

            string address = state.Address!;
            string wallet = state.Wallet!;

            // Cleared before the binding is read, so this slot cannot answer for itself.
            ClearPresence(ref state);
            liveCount--;

            // Suppressed only when the replacement actually has a presence of its own. The binding
            // alone is not enough: the legacy connect flow binds a wallet at AUTHENTICATED but places
            // it only on its first TeleportRequest, so a reconnect that drops before that teleport
            // would leave this exit suppressed and its own slot empty — no exit for the wallet at all
            // until the next snapshot.
            if (identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex live)
                && live != peer
                && live.Value < peerStates.Length
                && peerStates[live.Value].Realm is not null) return;

            feedPublisher.PublishParcelChange(address, realm, parcel: null);
        }
    }

    /// <summary>
    ///     Clears the presence columns alone. The clustering columns belong to the pass thread and are
    ///     retired by <see cref="ForgetVanishedPeers" />; writing the whole struct here would race it.
    /// </summary>
    private static void ClearPresence(ref PeerState state)
    {
        state.Wallet = null;
        state.Address = null;
        state.Realm = null;
        state.Parcel = default(ParcelCoord);
        state.OccupiedAt = 0;
    }

    /// <summary>
    ///     Every wallet with a published presence — the whole of what a snapshot carries — reduced
    ///     per address, not per slot (C1.3). Naming one wallet twice would leave a last-write-wins
    ///     consumer holding whichever entry happened to sort last, and would stop the batch order
    ///     being a function of its content, since two entries for one address tie under the
    ///     publisher's address-only comparator.
    /// </summary>
    private List<PeerPresence> CollectLivePresence()
    {
        var presence = new List<PeerPresence>(liveCount);
        var entryByAddress = new Dictionary<string, (int Entry, int Slot)>(liveCount, StringComparer.Ordinal);

        for (var index = 0; index < peerStates.Length; index++)
        {
            ref PeerState state = ref peerStates[index];

            if (state.Realm is not { } realm) continue;

            string address = state.Address!;
            var entry = new PeerPresence(address, realm, state.Parcel);

            if (!entryByAddress.TryGetValue(address, out (int Entry, int Slot) held))
            {
                entryByAddress[address] = (presence.Count, index);
                presence.Add(entry);

                continue;
            }

            if (!Supersedes(in state, in peerStates[held.Slot])) continue;

            // Replaced in place; the publisher sorts by address anyway.
            presence[held.Entry] = entry;
            entryByAddress[address] = (held.Entry, index);
        }

        return presence;
    }

    /// <summary>
    ///     Which of two slots holding one wallet is that wallet's presence. The slot the latest pass
    ///     saw wins: <see cref="TryCollectMember" /> collects only the wallet's live binding, so a
    ///     kicked connection stops being observed once the handshake rebinds the wallet.
    ///     <para />
    ///     Both seen in one pass means a single traversal straddled that rebind — the weakly-consistent
    ///     grid read reaching the kicked connection's cell before it and the incoming one's cell after.
    ///     There the newer <b>occupancy</b> wins. Occupancy, not placement: the kicked connection is
    ///     still on the wire and is often the one that moved most recently, so placement recency would
    ///     name the parcel the player just left.
    /// </summary>
    private static bool Supersedes(in PeerState candidate, in PeerState held) =>
        candidate.LastSeenPass != held.LastSeenPass
            ? candidate.LastSeenPass > held.LastSeenPass
            : candidate.OccupiedAt > held.OccupiedAt;

    /// <summary>
    ///     Clears carried-over state for peers absent from this pass. Mandatory rather than tidy:
    ///     <see cref="PeerIndex" /> is a recycled ENet slot, so state left behind would be inherited
    ///     by the next wallet on that slot and make its first assignment look unchanged.
    /// </summary>
    private void ForgetVanishedPeers()
    {
        for (var peerSlot = 0; peerSlot < peerStates.Length; peerSlot++)
        {
            ref PeerState state = ref peerStates[peerSlot];

            // An unstamped slot is already clear — never collected, or forgotten by an earlier pass.
            if (state.LastSeenPass == passNumber || state.LastSeenPass == 0) continue;

            // Clustering columns only. The presence columns are shared with OnPeerRemoved, which is
            // the one thing allowed to retire a presence entry (A1, C1.2); clearing the whole struct
            // here would race it and drop an exit. Zeroing LastSeenPass is what makes a slot that has
            // left the grid lose every Supersedes tie until then.
            state.PreviousPassClusterId = null;
            state.PublishedClusterId = null;
            state.PublishedRealm = null;
            state.CandidateClusterId = null;
            state.CandidateStreak = 0;
            state.LastSeenPass = 0;
        }
    }

    /// <summary>
    ///     Drops retained assignments for wallets absent for more than
    ///     <see cref="ClusterOptions.SessionRetentionPasses" /> passes, which also bounds the map to
    ///     concurrent wallets plus recently departed ones. Past the window there is no participant left
    ///     to name as displaced.
    /// </summary>
    private void ForgetExpiredSessions()
    {
        // Removing during enumeration is supported since .NET Core 3.0, same as PruneVanishedClusters.
        foreach ((string wallet, WalletAssignment assignment) in assignmentByWallet)
            if (passNumber - assignment.LastSeenPass > options.SessionRetentionPasses)
                assignmentByWallet.Remove(wallet);
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
        string Session,
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
    ///     What the tracker carries about one peer slot between passes, for both feeds.
    ///     <para />
    ///     Columns group by owning thread, which is the whole thread-safety argument: the presence
    ///     columns are written by the pass's publish walk <i>and</i> by <see cref="OnPeerRemoved" />
    ///     on a peer worker, so both take <see cref="presenceLock" />; the clustering columns are the
    ///     pass thread's alone and take none. Nothing may write the struct whole.
    ///     <para />
    ///     Two notions of "previous cluster", deliberately kept apart:
    ///     <see cref="PreviousPassClusterId" /> is what the last pass <i>computed</i>, which is what
    ///     sticky-ID inheritance measures overlap against; <see cref="PublishedClusterId" /> is what
    ///     the feed was last <i>told</i>, read only by the debounce decision. Conflating them starves
    ///     the debounce: a fragment mid-debounce would look unassigned to inheritance and be minted a
    ///     fresh ID every pass, so its candidate would never repeat and its streak never reach
    ///     <see cref="ClusterOptions.DwellPasses" />.
    ///     <para />
    ///     <see cref="Realm" /> and <see cref="PublishedRealm" /> are likewise distinct: the dwell
    ///     debounce can hold an assignment back while a parcel change goes out, so the realm the two
    ///     feeds last carried can differ.
    /// </summary>
    private struct PeerState
    {
        // --- Presence columns. Under presenceLock. Realm doubles as the occupancy flag.
        public string? Wallet;
        public string? Address;
        public string? Realm;
        public ParcelCoord Parcel;

        // The order this occupancy was acquired in — not the order its parcel was written in.
        // See Supersedes.
        public ulong OccupiedAt;

        // --- Clustering columns. Pass thread only.
        public string? PreviousPassClusterId;

        // Cluster and realm as last published together — the feed carries both, so either one
        // changing is a change.
        public string? PublishedClusterId;
        public string? PublishedRealm;

        public string? CandidateClusterId;
        public int CandidateStreak;

        // Pass this slot was last collected in. Zero means never, or forgotten since. Read by
        // Supersedes as well as by the sweep.
        public long LastSeenPass;
    }

    /// <summary>
    ///     A wallet's last published assignment and the session that published it, retained across the
    ///     <see cref="PeerIndex" /> change of a duplicate-session eviction: the outgoing session is
    ///     about to give up its own slot, so slot-keyed state could not survive the change.
    ///     <para />
    ///     <see cref="LastSeenPass" /> tracks when the wallet was last <i>seen</i> rather than last
    ///     published: a stationary peer publishes once and never again, and its entry must not expire
    ///     underneath it.
    /// </summary>
    private struct WalletAssignment
    {
        public string ClusterId;
        public string Realm;
        public string Session;
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
