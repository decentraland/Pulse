using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Clusters;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class ClusterTrackerTests
{
    private const int MAX_PEERS = 100;
    private const int RING_CAPACITY = 4;
    private const float CELL_SIZE = 50f;

    private const string REALM = "realm-a";
    private const string OTHER_REALM = "realm-b";
    private const string WALLET = "0xduplicate";

    private RealmSpatialGrids grids;
    private SnapshotBoard snapshotBoard;
    private IdentityBoard identityBoard;
    private ClusterBoard clusterBoard;
    private IClusterFeedPublisher feedPublisher;

    [SetUp]
    public void SetUp()
    {
        grids = new RealmSpatialGrids(CELL_SIZE, MAX_PEERS);
        snapshotBoard = new SnapshotBoard(MAX_PEERS, RING_CAPACITY);
        identityBoard = new IdentityBoard(MAX_PEERS);
        clusterBoard = new ClusterBoard();
        feedPublisher = Substitute.For<IClusterFeedPublisher>();
    }

    [Test]
    public void SinglePeer_FormsSingletonCluster()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));

        tracker.RunPass();

        ClusterPass pass = clusterBoard.Current;
        Assert.That(pass.Clusters, Has.Count.EqualTo(1));
        Assert.That(pass.Clusters[0].Count, Is.EqualTo(1));
        Assert.That(pass.Clusters[0].Realm, Is.EqualTo(REALM));
        Assert.That(pass.GetClusterId(new PeerIndex(0)), Is.EqualTo("C1"));
    }

    [Test]
    public void PeersInSameCell_FormOneCluster()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(20, 0, 20));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1));
        Assert.That(clusterBoard.Current.Clusters[0].Count, Is.EqualTo(2));
    }

    /// <summary>
    ///     Reproduces a split observed on a deployed server: two groups 100 u apart merged at the
    ///     origin but stayed separate at x=300/400, and an eight-cell chain at a uniform 100 u pitch
    ///     broke into two halves at exactly that pair. Distance and cell adjacency are identical in
    ///     both cases, so if adjacency is purely positional these must agree.
    /// </summary>
    [TestCase(0f, 100f, TestName = "AdjacentCellsAtOrigin_FormOneCluster")]
    [TestCase(300f, 400f, TestName = "AdjacentCellsAwayFromOrigin_FormOneCluster")]
    public void PeersOneCellApart_FormOneCluster(float firstX, float secondX)
    {
        var farGrids = new RealmSpatialGrids(100f, MAX_PEERS);
        grids = farGrids;

        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(firstX, 0, 0));
        SetupPeer(new PeerIndex(1), new Vector3(secondX, 0, 0));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1),
            $"peers at x={firstX} and x={secondX} are one cell apart and must share a cluster");
    }

    /// <summary>
    ///     The same adjacency question, placed the way production places peers: through
    ///     <see cref="PeerSnapshotPublisher" />, which decodes a parcel index plus a quantized
    ///     in-parcel offset into the global position the grid is keyed on. Coordinates are kept off
    ///     the cell boundaries — see
    ///     <see cref="PublishingOnACellBoundary_CanLandInTheLowerCell" /> for why that matters.
    /// </summary>
    [TestCase(10f, 110f, TestName = "PublishedThroughPublisher_NearOrigin_FormOneCluster")]
    [TestCase(350f, 450f, TestName = "PublishedThroughPublisher_AwayFromOrigin_FormOneCluster")]
    public void PeersPublishedOneCellApart_FormOneCluster(float firstX, float secondX)
    {
        grids = new RealmSpatialGrids(100f, MAX_PEERS);

        var parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        ITimeProvider timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(1u);

        var publisher = new PeerSnapshotPublisher(snapshotBoard, grids, parcelEncoder, timeProvider);

        PublishAt(publisher, parcelEncoder, new PeerIndex(0), firstX);
        PublishAt(publisher, parcelEncoder, new PeerIndex(1), secondX);

        ClusterTracker tracker = CreateTracker();
        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1),
            $"peers published at x={firstX} and x={secondX} are one cell apart and must share a cluster");
    }

    /// <summary>
    ///     A peer published at exactly a cell boundary can be indexed in the cell <em>below</em> it.
    ///     The in-parcel offset is quantized to 8 bits over [0, 16] (step ≈ 0.0627), and an offset of
    ///     12 encodes to 191 rather than 191.25, decoding to 11.984 — so global x=300 is stored as
    ///     299.984 and lands in cell 2, not cell 3.
    ///     <para />
    ///     Harmless in itself: the two cells are adjacent, so a peer's clustering relative to its
    ///     neighbours does not change. It matters for <em>test layouts</em>. A live run that spaced
    ///     eight groups at exact multiples of 100 saw them land in cells 0,1,2,2,4,5,6,6 — leaving
    ///     cell 3 empty and splitting what looked like an unbroken chain into two clusters. Space
    ///     fixtures off the boundaries, or assert against the decoded position rather than the input.
    /// </summary>
    [Test]
    public void PublishingOnACellBoundary_CanLandInTheLowerCell()
    {
        const float CELL = 100f;
        grids = new RealmSpatialGrids(CELL, MAX_PEERS);

        var parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        ITimeProvider timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(1u);

        var publisher = new PeerSnapshotPublisher(snapshotBoard, grids, parcelEncoder, timeProvider);

        PublishAt(publisher, parcelEncoder, new PeerIndex(0), 300f);

        Assert.That(snapshotBoard.TryRead(new PeerIndex(0), out PeerSnapshot snapshot), Is.True);

        float stored = snapshot.GlobalPosition.X;

        Assert.That(stored, Is.LessThan(300f), "the offset quantizes down, so the stored position is below the input");
        Assert.That(300f - stored, Is.LessThan(Decentraland.Pulse.PlayerState.PositionXQuantizedStep),
            "and by less than one quantization step");
        Assert.That(grids.CellCoord(stored), Is.EqualTo(2), "which puts it one cell below floor(300/100)");
    }

    private void PublishAt(PeerSnapshotPublisher publisher, ParcelEncoder parcelEncoder, PeerIndex peer, float x)
    {
        snapshotBoard.SetActive(peer);
        identityBoard.Set(peer, $"0xwallet{peer.Value}");

        int index = parcelEncoder.EncodeFromGlobalPosition(new Vector3(x, 0, 0), out Vector3 local);

        var state = new Decentraland.Pulse.PlayerState { ParcelIndex = index };
        state.PositionXQuantized = local.X;
        state.PositionYQuantized = local.Y;
        state.PositionZQuantized = local.Z;

        publisher.PublishFromPlayerState(peer, state, realm: REALM);
    }

    /// <summary>
    ///     Two clusters that become adjacent must merge. On a deployed server, groups that connected
    ///     in separate waves stayed split even though their cells were adjacent, while groups that
    ///     arrived together merged — so the distinguishing variable is whether a cluster already
    ///     existed when the second group appeared, not the geometry.
    /// </summary>
    [Test]
    public void ClusterFormedBeforeNeighbourArrives_MergesOnNextPass()
    {
        grids = new RealmSpatialGrids(100f, MAX_PEERS);

        ClusterTracker tracker = CreateTracker();

        SetupPeer(new PeerIndex(0), new Vector3(300, 0, 0));
        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1), "first group forms its own cluster");

        SetupPeer(new PeerIndex(1), new Vector3(400, 0, 0));
        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1),
            "an adjacent cell appearing later must join the existing cluster, not start a second one");
        Assert.That(clusterBoard.Current.Clusters[0].Count, Is.EqualTo(2));
    }

    /// <summary>
    ///     The chaining worst case from <c>docs/clustering-on-aoi.md</c> §3.2, at the smallest scale
    ///     that still chains: eight occupied cells in a line, each adjacent to the next, must be a
    ///     single connected component.
    ///     <para />
    ///     Places peers directly, so the cells really are 0–7. Sending the same coordinates over the
    ///     wire does not reproduce this — see
    ///     <see cref="PublishingOnACellBoundary_CanLandInTheLowerCell" />.
    /// </summary>
    [Test]
    public void EightCellsInALine_FormOneCluster()
    {
        grids = new RealmSpatialGrids(100f, MAX_PEERS);

        ClusterTracker tracker = CreateTracker();

        for (var cell = 0; cell < 8; cell++)
            SetupPeer(new PeerIndex((uint)cell), new Vector3(cell * 100f, 0, 0));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1),
            "eight cells at a uniform one-cell pitch form one chain");
        Assert.That(clusterBoard.Current.Clusters[0].Count, Is.EqualTo(8));
    }

    [Test]
    public void PeersInAdjacentCells_FormOneCluster()
    {
        ClusterTracker tracker = CreateTracker();

        // Cell x=0 and cell x=1 — 8-neighbour adjacent, so transitively one cluster.
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(60, 0, 10));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1));
        Assert.That(clusterBoard.Current.Clusters[0].Count, Is.EqualTo(2));
    }

    /// <summary>
    ///     All 8 neighbor offsets must merge, even though a pass probes only the four pointing ahead —
    ///     the other four are reached from the neighbor's own probe.
    /// </summary>
    [TestCase(-1, -1)]
    [TestCase(-1, 0)]
    [TestCase(-1, 1)]
    [TestCase(0, -1)]
    [TestCase(0, 1)]
    [TestCase(1, -1)]
    [TestCase(1, 0)]
    [TestCase(1, 1)]
    public void PeersInAnyNeighbouringCell_FormOneCluster(int dx, int dz)
    {
        ClusterTracker tracker = CreateTracker();

        // Mid-cell so the offset lands squarely in the neighbouring cell either way.
        var origin = new Vector3(125, 0, 125);

        SetupPeer(new PeerIndex(0), origin);
        SetupPeer(new PeerIndex(1), origin + new Vector3(dx * CELL_SIZE, 0, dz * CELL_SIZE));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1));
        Assert.That(clusterBoard.Current.Clusters[0].Count, Is.EqualTo(2));
    }

    [Test]
    public void ChainedCells_FormOneClusterTransitively()
    {
        ClusterTracker tracker = CreateTracker();

        // Cells x=0,1,2: the ends are not neighbours, the middle bridges them.
        SetupPeer(new PeerIndex(0), new Vector3(25, 0, 25));
        SetupPeer(new PeerIndex(1), new Vector3(75, 0, 25));
        SetupPeer(new PeerIndex(2), new Vector3(125, 0, 25));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1));
        Assert.That(clusterBoard.Current.Clusters[0].Count, Is.EqualTo(3));
    }

    [Test]
    public void PeersBeyondNeighbouringCells_FormSeparateClusters()
    {
        ClusterTracker tracker = CreateTracker();

        // Cell x=0 and cell x=3 — not neighbours, and nothing bridges the gap.
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(160, 0, 10));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(2));
    }

    [Test]
    public void PeersInDifferentRealms_NeverShareACluster()
    {
        ClusterTracker tracker = CreateTracker();

        // Same cell in both realms, so only the grid partition can separate them.
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(12, 0, 12), OTHER_REALM);

        tracker.RunPass();

        ClusterPass pass = clusterBoard.Current;
        Assert.That(pass.Clusters, Has.Count.EqualTo(2));
        Assert.That(pass.Clusters.Select(i => i.Realm), Is.EquivalentTo(new[] { REALM, OTHER_REALM }));
        Assert.That(pass.GetClusterId(new PeerIndex(0)), Is.Not.EqualTo(pass.GetClusterId(new PeerIndex(1))));
    }

    [Test]
    public void AdjacentCellsInDifferentRealms_AreNotNeighbours()
    {
        ClusterTracker tracker = CreateTracker();

        // Cells are indexed per realm, so a neighbour probe must not reach a cell of another realm.
        // realm-b straddles realm-a's cell on both sides, so whichever realm the tracker walks first,
        // the other one probes across it.
        SetupPeer(new PeerIndex(0), new Vector3(75, 0, 25));                    // realm-a, cell x=1
        SetupPeer(new PeerIndex(1), new Vector3(25, 0, 25), OTHER_REALM);       // realm-b, cell x=0
        SetupPeer(new PeerIndex(2), new Vector3(125, 0, 25), OTHER_REALM);      // realm-b, cell x=2

        tracker.RunPass();

        ClusterPass pass = clusterBoard.Current;

        // realm-a's single cell, plus realm-b's two cells that are not adjacent to each other.
        Assert.That(pass.Clusters, Has.Count.EqualTo(3));
        Assert.That(pass.Clusters.Select(c => c.Count), Is.All.EqualTo(1));
        Assert.That(pass.Clusters.Count(c => c.Realm == OTHER_REALM), Is.EqualTo(2));
    }

    [Test]
    public void RealmlessPeer_IsExcludedFromClustering()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), realm: null);

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Is.Empty);
        Assert.That(clusterBoard.Current.GetClusterId(new PeerIndex(0)), Is.Null);
    }

    [Test]
    public void PeerWithoutWallet_IsExcludedFromClustering()
    {
        ClusterTracker tracker = CreateTracker();

        // In the grid and snapshot board, but never registered in the identity board.
        grids.Set(new PeerIndex(0), REALM, new Vector3(10, 0, 10));
        snapshotBoard.SetActive(new PeerIndex(0));
        PublishSnapshot(new PeerIndex(0), new Vector3(10, 0, 10), REALM);

        tracker.RunPass();

        Assert.That(clusterBoard.Current.Clusters, Is.Empty);
    }

    [Test]
    public void StableCrowd_KeepsClusterIdAcrossPasses()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(20, 0, 20));

        tracker.RunPass();
        string? first = clusterBoard.Current.GetClusterId(new PeerIndex(0));

        tracker.RunPass();
        tracker.RunPass();

        Assert.That(clusterBoard.Current.GetClusterId(new PeerIndex(0)), Is.EqualTo(first));
    }

    [Test]
    public void Split_LargerFragmentInheritsTheClusterId()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(20, 0, 20));
        SetupPeer(new PeerIndex(2), new Vector3(30, 0, 30));

        tracker.RunPass();
        string? original = clusterBoard.Current.GetClusterId(new PeerIndex(0));

        // One peer leaves; the remaining pair shares more members with the original cluster.
        MovePeer(new PeerIndex(2), new Vector3(500, 0, 500));
        tracker.RunPass();

        ClusterPass pass = clusterBoard.Current;
        Assert.That(pass.Clusters, Has.Count.EqualTo(2));
        Assert.That(pass.GetClusterId(new PeerIndex(0)), Is.EqualTo(original));
        Assert.That(pass.GetClusterId(new PeerIndex(1)), Is.EqualTo(original));
        Assert.That(pass.GetClusterId(new PeerIndex(2)), Is.Not.EqualTo(original));
    }

    [Test]
    public void Merge_InheritsIdOfTheLargestContributor()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(20, 0, 20));
        SetupPeer(new PeerIndex(2), new Vector3(30, 0, 30));
        SetupPeer(new PeerIndex(3), new Vector3(500, 0, 500));

        tracker.RunPass();

        string? crowdId = clusterBoard.Current.GetClusterId(new PeerIndex(0));
        string? lonerId = clusterBoard.Current.GetClusterId(new PeerIndex(3));
        Assert.That(crowdId, Is.Not.EqualTo(lonerId));

        // The lone peer joins the crowd: three shared members beat one.
        MovePeer(new PeerIndex(3), new Vector3(25, 0, 25));
        tracker.RunPass();

        ClusterPass pass = clusterBoard.Current;
        Assert.That(pass.Clusters, Has.Count.EqualTo(1));
        Assert.That(pass.Clusters[0].Id, Is.EqualTo(crowdId));
        Assert.That(pass.GetClusterId(new PeerIndex(3)), Is.EqualTo(crowdId));
    }

    [Test]
    public void FirstAssignment_PublishesImmediatelyDespiteDwell()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange("0xwallet0", "C1", REALM);
    }

    [Test]
    public void Reassignment_PublishesOnlyAfterDwellPassesAgree()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        SetupCrowdOfThree();

        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        // Peer 2 leaves a three-peer crowd, so the pair keeps the original ID outright and peer 2 is
        // unambiguously the fragment that must take a new one.
        MovePeer(new PeerIndex(2), new Vector3(500, 0, 500));

        tracker.RunPass();
        feedPublisher.DidNotReceive().PublishClusterChange("0xwallet2", Arg.Any<string>(), Arg.Any<string>());

        tracker.RunPass();
        feedPublisher.DidNotReceive().PublishClusterChange("0xwallet2", Arg.Any<string>(), Arg.Any<string>());

        tracker.RunPass();
        feedPublisher.Received(1).PublishClusterChange("0xwallet2", Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void Teleport_BypassesTheDwellDebounce()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        SetupCrowdOfThree();

        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        MovePeer(new PeerIndex(2), new Vector3(500, 0, 500), isTeleport: true);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange("0xwallet2", Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void RealmChange_BypassesTheDwellDebounce()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        SetupCrowdOfThree();

        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        // Same position, different realm — the realm partition alone moves it to a new cluster.
        MovePeer(new PeerIndex(2), new Vector3(30, 0, 30), realm: OTHER_REALM);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange("0xwallet2", Arg.Any<string>(), OTHER_REALM);
    }

    [Test]
    public void ClusterDeletion_BypassesTheDwellDebounce()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        SetupCrowdOfThree();
        SetupPeer(new PeerIndex(3), new Vector3(500, 0, 500));

        tracker.RunPass();
        string crowdId = ClusterIdOf(new PeerIndex(0));
        feedPublisher.ClearReceivedCalls();

        // The loner's own cluster ceases to exist when it merges into the crowd, so waiting three passes
        // would leave it published under a cluster nobody is in.
        MovePeer(new PeerIndex(3), new Vector3(25, 0, 25));
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange("0xwallet3", crowdId, REALM);
    }

    /// <summary>
    ///     A crowd that changes realm together overlaps itself completely, so it keeps its sticky ID.
    ///     The feed carries the realm alongside the ID, so the change is still published.
    /// </summary>
    [Test]
    public void CrowdChangingRealmTogether_RepublishesWithTheNewRealm()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        SetupCrowdOfThree();

        tracker.RunPass();
        string clusterId = ClusterIdOf(new PeerIndex(0));
        feedPublisher.ClearReceivedCalls();

        MovePeer(new PeerIndex(0), new Vector3(10, 0, 10), OTHER_REALM);
        MovePeer(new PeerIndex(1), new Vector3(20, 0, 20), OTHER_REALM);
        MovePeer(new PeerIndex(2), new Vector3(30, 0, 30), OTHER_REALM);
        tracker.RunPass();

        Assert.That(ClusterIdOf(new PeerIndex(0)), Is.EqualTo(clusterId));
        feedPublisher.Received(1).PublishClusterChange("0xwallet0", clusterId, OTHER_REALM);
        feedPublisher.Received(1).PublishClusterChange("0xwallet1", clusterId, OTHER_REALM);
        feedPublisher.Received(1).PublishClusterChange("0xwallet2", clusterId, OTHER_REALM);
    }

    [Test]
    public void UnchangedAssignment_IsNotRepublished()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));

        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();
        tracker.RunPass();

        feedPublisher.DidNotReceive().PublishClusterChange(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public void Topology_IsPublishedOncePerPass()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));

        tracker.RunPass();
        tracker.RunPass();

        feedPublisher.Received(2).PublishTopology(Arg.Any<ClusterPass>());
    }

    [Test]
    public void ClusterGeometry_IsCentroidAndFarthestMemberDistance()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 20));
        SetupPeer(new PeerIndex(1), new Vector3(30, 0, 20));

        tracker.RunPass();

        ClusterInfo cluster = clusterBoard.Current.Clusters[0];
        Assert.That(cluster.Centroid.X, Is.EqualTo(20f).Within(0.001f));
        Assert.That(cluster.Centroid.Z, Is.EqualTo(20f).Within(0.001f));
        Assert.That(cluster.Radius, Is.EqualTo(10f).Within(0.001f));
    }

    [Test]
    public void DepartedPeer_LosesItsCarriedAssignment()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));

        tracker.RunPass();
        Assert.That(clusterBoard.Current.GetClusterId(new PeerIndex(0)), Is.EqualTo("C1"));

        // Simulate a disconnect: the peer leaves the grid and its snapshot slot is released.
        RemovePeer(new PeerIndex(0));
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();
        Assert.That(clusterBoard.Current.Clusters, Is.Empty);

        // The recycled slot must be treated as a first assignment, not an unchanged one: PeerIndex is an
        // ENet slot, and the next wallet to land here is a different player.
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), wallet: "0xreused");
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange("0xreused", Arg.Any<string>(), REALM);
    }

    [Test]
    public async Task Disabled_RunsNoPass()
    {
        ClusterTracker tracker = CreateTracker(enabled: false);
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));

        await tracker.StartAsync(CancellationToken.None);
        await tracker.StopAsync(CancellationToken.None);

        Assert.That(clusterBoard.Current.Clusters, Is.Empty);
        feedPublisher.DidNotReceive().PublishTopology(Arg.Any<ClusterPass>());
    }

    [Test]
    public void OutgoingDuplicateSession_IsNotClustered()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        // Far enough apart to form two clusters if both were collected. SetupPeer's identityBoard.Set
        // rebinds the wallet to `incoming`, exactly as the duplicate-session handshake does before the
        // outgoing peer's transport disconnect lands.
        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        Assert.That(clusterBoard.Current.GetClusterId(outgoing), Is.Null);
        Assert.That(clusterBoard.Current.GetClusterId(incoming), Is.Not.Null);
        Assert.That(clusterBoard.Current.Clusters, Has.Count.EqualTo(1));
    }

    [Test]
    public void OutgoingDuplicateSession_DoesNotPublishOnTheWalletsSubject()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), wallet: WALLET);
        SetupPeer(new PeerIndex(1), new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, Arg.Any<string>(), REALM);
    }

    [Test]
    public void DuplicatedWallet_AppearsOnceInThePassRoster()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), wallet: WALLET);
        SetupPeer(new PeerIndex(1), new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        // FillIslandStatus adds one roster entry per ClusterPeerInfo with no de-duplication, so a
        // second entry here would list the wallet in two islands of one engine.islands snapshot.
        Assert.That(clusterBoard.Current.Peers.Count(info => info.Wallet == WALLET), Is.EqualTo(1));
    }

    [Test]
    public void StaleWalletWithNoLiveBinding_IsNotClustered()
    {
        ClusterTracker tracker = CreateTracker();
        var stale = new PeerIndex(0);

        // Still in the grid awaiting its own disconnect, but the wallet has since moved to a peer
        // that has also fully disconnected — the reverse lookup for it now finds nothing at all,
        // exercising the guard's miss branch rather than its found-but-different-peer branch.
        SetupPeer(stale, new Vector3(10, 0, 10), wallet: WALLET);
        identityBoard.Set(new PeerIndex(1), WALLET);
        identityBoard.Remove(new PeerIndex(1));

        tracker.RunPass();

        Assert.That(clusterBoard.Current.GetClusterId(stale), Is.Null);
        Assert.That(clusterBoard.Current.Clusters, Is.Empty);
    }

    /// <summary>
    ///     A replacement session must be published into the LiveKit room the outgoing session still
    ///     holds, not the cluster its own position would compute to, so LiveKit's duplicate-identity
    ///     rule can supersede the outgoing participant.
    /// </summary>
    [Test]
    public void IncomingSession_IsFirstPublishedIntoTheOutgoingSessionsCluster()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        feedPublisher.ClearReceivedCalls();

        // The replacement arrives far away, so its own cluster differs from the outgoing one.
        RemovePeer(outgoing);
        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);
    }

    /// <summary>
    ///     The migration off a handover is exempt from the dwell debounce, so the peer's own cluster
    ///     follows in the very next pass rather than being held in the outgoing session's LiveKit room
    ///     for <see cref="ClusterOptions.DwellPasses" /> passes.
    /// </summary>
    [Test]
    public void AfterAHandover_ThePeersOwnClusterFollowsImmediatelyDespiteDwell()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        RemovePeer(outgoing);
        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        string ownCluster = ClusterIdOf(incoming);
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();

        // One pass, not DwellPasses.
        feedPublisher.Received(1).PublishClusterChange(WALLET, ownCluster, REALM);
    }

    [Test]
    public void ReconnectIntoTheSameCluster_PublishesOnce()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        // Two bystanders keep the crowd's sticky ID alive across the session change, so the
        // replacement lands in the very cluster the ledger remembers.
        SetupPeer(new PeerIndex(2), new Vector3(20, 0, 20));
        SetupPeer(new PeerIndex(3), new Vector3(30, 0, 30));
        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string crowdCluster = ClusterIdOf(outgoing);
        feedPublisher.ClearReceivedCalls();

        RemovePeer(outgoing);
        SetupPeer(incoming, new Vector3(10, 0, 10), wallet: WALLET);

        tracker.RunPass();
        tracker.RunPass();

        Assert.That(ClusterIdOf(incoming), Is.EqualTo(crowdCluster));
        feedPublisher.Received(1).PublishClusterChange(WALLET, crowdCluster, REALM);
    }

    [Test]
    public void AfterAHandoverIntoASurvivingCluster_TheOwnClusterFollowsWithoutWaitingOutTheDwell()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        // Two bystanders keep the outgoing session's cluster alive across the session change, so the
        // migration off the handover cannot be waved through by the cluster-deletion bypass.
        SetupPeer(new PeerIndex(2), new Vector3(20, 0, 20));
        SetupPeer(new PeerIndex(3), new Vector3(30, 0, 30));
        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string crowdCluster = ClusterIdOf(outgoing);
        feedPublisher.ClearReceivedCalls();

        RemovePeer(outgoing);
        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, crowdCluster, REALM);

        string ownCluster = ClusterIdOf(incoming);
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, ownCluster, REALM);
    }

    /// <summary>
    ///     The outgoing session's LiveKit room outlives the cluster it was published into, so the
    ///     handover must fire even when that cluster no longer exists by the time the replacement
    ///     arrives.
    /// </summary>
    [Test]
    public void Handover_FiresEvenWhenTheRememberedClusterNoLongerExists()
    {
        ClusterTracker tracker = CreateTracker();
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);

        // A pass with the wallet absent, so its cluster is pruned and is demonstrably not live when the
        // replacement arrives.
        tracker.RunPass();
        Assert.That(clusterBoard.Current.Clusters, Is.Empty);
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);
    }

    /// <summary>
    ///     The room the replacement must collide in is still the outgoing session's, so a handover
    ///     across a realm change carries the retained realm along with the retained cluster rather than
    ///     the replacement's own.
    /// </summary>
    [Test]
    public void Handover_CarriesTheRememberedRealmWhenTheSessionsRealmsDiffer()
    {
        ClusterTracker tracker = CreateTracker(dwellPasses: 3);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);
        feedPublisher.ClearReceivedCalls();

        // The replacement authenticates into a different world.
        SetupPeer(incoming, new Vector3(10, 0, 10), OTHER_REALM, WALLET);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);

        string ownCluster = ClusterIdOf(incoming);
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, ownCluster, OTHER_REALM);
    }

    [Test]
    public void RecycledSlotWithADifferentWallet_DoesNotInheritAHandover()
    {
        ClusterTracker tracker = CreateTracker();
        var peer = new PeerIndex(0);

        SetupPeer(peer, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string firstCluster = ClusterIdOf(peer);
        RemovePeer(peer);
        feedPublisher.ClearReceivedCalls();

        // A pass with the slot empty, as a real disconnect would leave it, so the recycled-slot
        // PeerClusterState is forgotten before the next tenant arrives — isolating this test to the
        // wallet ledger rather than also exercising ForgetVanishedPeers' own slot-recycling contract
        // (see DepartedPeer_LosesItsCarriedAssignment).
        tracker.RunPass();

        // The ledger is keyed by wallet, not by the recycled ENet slot, so the next tenant inherits
        // nothing.
        SetupPeer(peer, new Vector3(500, 0, 500), wallet: "0xother");
        tracker.RunPass();

        feedPublisher.DidNotReceive().PublishClusterChange("0xother", firstCluster, Arg.Any<string>());
        feedPublisher.Received(1).PublishClusterChange("0xother", ClusterIdOf(peer), REALM);
    }

    [Test]
    public void HandoverEntry_ExpiresAfterHandoverPasses()
    {
        ClusterTracker tracker = CreateTracker(handoverPasses: 2);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);

        tracker.RunPass();
        tracker.RunPass();
        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.DidNotReceive().PublishClusterChange(WALLET, outgoingCluster, Arg.Any<string>());
        feedPublisher.Received(1).PublishClusterChange(WALLET, ClusterIdOf(incoming), REALM);
    }

    [Test]
    public void HandoverEntry_SurvivesAWalletThatPublishesNothingForLongerThanTheWindow()
    {
        ClusterTracker tracker = CreateTracker(handoverPasses: 2);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);

        // Standing still: the assignment is unchanged so nothing is published, but the entry must be
        // refreshed on every pass the wallet is seen or it would expire under a stationary player.
        tracker.RunPass();
        tracker.RunPass();
        tracker.RunPass();
        tracker.RunPass();

        RemovePeer(outgoing);
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange(WALLET, outgoingCluster, REALM);
    }

    [Test]
    public void HandoverPassesZero_DisablesTheHandover()
    {
        ClusterTracker tracker = CreateTracker(handoverPasses: 0);
        var outgoing = new PeerIndex(0);
        var incoming = new PeerIndex(1);

        SetupPeer(outgoing, new Vector3(10, 0, 10), wallet: WALLET);
        tracker.RunPass();

        string outgoingCluster = ClusterIdOf(outgoing);
        RemovePeer(outgoing);
        feedPublisher.ClearReceivedCalls();

        SetupPeer(incoming, new Vector3(500, 0, 500), wallet: WALLET);
        tracker.RunPass();

        feedPublisher.DidNotReceive().PublishClusterChange(WALLET, outgoingCluster, Arg.Any<string>());
        feedPublisher.Received(1).PublishClusterChange(WALLET, ClusterIdOf(incoming), REALM);
    }

    private ClusterTracker CreateTracker(bool enabled = true, int dwellPasses = 1, int handoverPasses = 15)
    {
        // Options.Create rather than a substitute: IOptions<T> has a real, trivial implementation, and
        // a substituted property getter depends on NSubstitute's ambient call context — which
        // neighbouring tests in this fixture were perturbing, so `enabled: false` silently arrived as
        // the default.
        IOptions<ClusterOptions> options = Options.Create(new ClusterOptions
        {
            Enabled = enabled,
            PassIntervalMs = 1000,
            DwellPasses = dwellPasses,
            IdPrefix = "C",
            HandoverPasses = handoverPasses,
        });

        return new ClusterTracker(
            NullLogger<ClusterTracker>.Instance,
            options,
            grids,
            snapshotBoard,
            identityBoard,
            clusterBoard,
            feedPublisher,
            MAX_PEERS);
    }

    /// <summary>
    ///     The cluster the peer belongs to as of the last published pass. Which of several components is
    ///     minted C1 depends on grid enumeration order, so the ID cannot be spelled out as a literal.
    /// </summary>
    private string ClusterIdOf(PeerIndex peer) =>
        clusterBoard.Current.Peers.Single(info => info.Peer.Equals(peer)).ClusterId;

    /// <summary>
    ///     Three co-located peers, so moving one out leaves a fragment that wins the sticky ID outright —
    ///     an even 1-versus-1 split ties on shared members, and which side keeps the ID is arbitrary.
    /// </summary>
    private void SetupCrowdOfThree()
    {
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(20, 0, 20));
        SetupPeer(new PeerIndex(2), new Vector3(30, 0, 30));
    }

    private void SetupPeer(
        PeerIndex peer,
        Vector3 position,
        string? realm = REALM,
        string? wallet = null)
    {
        // A realmless peer is placed in no grid at all, matching what the publisher does.
        if (realm is not null)
            grids.Set(peer, realm, position);

        snapshotBoard.SetActive(peer);
        identityBoard.Set(peer, wallet ?? $"0xwallet{peer.Value}");
        PublishSnapshot(peer, position, realm);
    }

    /// <summary>
    ///     Tears a peer down the way the simulation's disconnect cleanup does: out of the grid, its
    ///     snapshot slot released, its identity wiped.
    /// </summary>
    private void RemovePeer(PeerIndex peer)
    {
        grids.Remove(peer);
        snapshotBoard.ClearActive(peer);
        identityBoard.Remove(peer);
    }

    private void MovePeer(
        PeerIndex peer,
        Vector3 position,
        string? realm = REALM,
        bool isTeleport = false)
    {
        if (realm is not null)
            grids.Set(peer, realm, position);

        PublishSnapshot(peer, position, realm, isTeleport);
    }

    private void PublishSnapshot(
        PeerIndex peer,
        Vector3 position,
        string? realm,
        bool isTeleport = false)
    {
        // The tracker reads GlobalPosition, Realm, Parcel and IsTeleport; the quantized parcel-local
        // codes are left at their defaults.
        snapshotBoard.Publish(peer, TestSnapshots.Make(
            seq: snapshotBoard.LastSeq(peer) + 1,
            globalPosition: position,
            realm: realm,
            isTeleport: isTeleport));
    }
}
