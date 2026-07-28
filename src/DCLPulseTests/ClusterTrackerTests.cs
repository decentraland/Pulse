using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Clusters;
using Pulse.Peers;
using Pulse.Peers.Simulation;
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

    private SpatialGrid grid;
    private SnapshotBoard snapshotBoard;
    private IdentityBoard identityBoard;
    private ClusterBoard clusterBoard;
    private IClusterFeedPublisher feedPublisher;

    [SetUp]
    public void SetUp()
    {
        grid = new SpatialGrid(CELL_SIZE, MAX_PEERS);
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
    ///     Every one of the 8 neighbor offsets must merge, even though a pass only probes the four
    ///     that point "ahead" — the other four are reached from the neighbor's own probe.
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

        // Same cell, so only the realm partition can separate them.
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10));
        SetupPeer(new PeerIndex(1), new Vector3(12, 0, 12), OTHER_REALM);

        tracker.RunPass();

        ClusterPass pass = clusterBoard.Current;
        Assert.That(pass.Clusters, Has.Count.EqualTo(2));
        Assert.That(pass.Clusters.Select(i => i.Realm), Is.EquivalentTo(new[] { REALM, OTHER_REALM }));
        Assert.That(pass.GetClusterId(new PeerIndex(0)), Is.Not.EqualTo(pass.GetClusterId(new PeerIndex(1))));
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

        // Placed in the grid and snapshot board but never registered in the identity board, so it
        // cannot be addressed on the feed.
        grid.Set(new PeerIndex(0), new Vector3(10, 0, 10));
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

        // Peer 2 leaves a three-peer crowd, so the pair it left behind keeps the original ID
        // outright and peer 2 is unambiguously the fragment that must take a new one.
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

        // The loner's own cluster ceases to exist when it merges into the crowd, so waiting three
        // passes would leave it addressed by a cluster nobody is in.
        MovePeer(new PeerIndex(3), new Vector3(25, 0, 25));
        tracker.RunPass();

        feedPublisher.Received(1).PublishClusterChange("0xwallet3", crowdId, REALM);
    }

    /// <summary>
    ///     A crowd that changes realm together overlaps itself completely, so it keeps its sticky
    ///     ID. The feed carries the realm alongside the ID, so the change still has to be published.
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
        grid.Remove(new PeerIndex(0));
        snapshotBoard.ClearActive(new PeerIndex(0));
        identityBoard.Remove(new PeerIndex(0));
        feedPublisher.ClearReceivedCalls();

        tracker.RunPass();
        Assert.That(clusterBoard.Current.Clusters, Is.Empty);

        // The recycled slot must be treated as a first assignment, not an unchanged one — PeerIndex
        // is an ENet slot and the next wallet to land here is a different player.
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

    private ClusterTracker CreateTracker(bool enabled = true, int dwellPasses = 1)
    {
        var options = Substitute.For<IOptions<ClusterOptions>>();

        options.Value.Returns(new ClusterOptions
        {
            Enabled = enabled,
            PassIntervalMs = 1000,
            DwellPasses = dwellPasses,
            IdPrefix = "C",
        });

        return new ClusterTracker(
            NullLogger<ClusterTracker>.Instance,
            options,
            grid,
            snapshotBoard,
            identityBoard,
            clusterBoard,
            feedPublisher,
            MAX_PEERS);
    }

    /// <summary>
    ///     The cluster the peer belongs to as of the last published pass. Used where a test needs
    ///     the ID itself rather than just a comparison: which of several components is minted C1
    ///     depends on grid enumeration order, so the ID cannot be spelled out as a literal.
    /// </summary>
    private string ClusterIdOf(PeerIndex peer) =>
        clusterBoard.Current.Peers.Single(info => info.Peer.Equals(peer)).ClusterId;

    /// <summary>
    ///     Three co-located peers. Used wherever a test moves one peer out and needs the remaining
    ///     fragment to win the sticky ID outright — an even 1-versus-1 split ties on shared members,
    ///     and which side keeps the ID is then arbitrary.
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
        grid.Set(peer, position);
        snapshotBoard.SetActive(peer);
        identityBoard.Set(peer, wallet ?? $"0xwallet{peer.Value}");
        PublishSnapshot(peer, position, realm);
    }

    private void MovePeer(
        PeerIndex peer,
        Vector3 position,
        string? realm = REALM,
        bool isTeleport = false)
    {
        grid.Set(peer, position);
        PublishSnapshot(peer, position, realm, isTeleport);
    }

    private void PublishSnapshot(
        PeerIndex peer,
        Vector3 position,
        string? realm,
        bool isTeleport = false)
    {
        // The tracker reads GlobalPosition, Realm, Parcel and IsTeleport; the quantized
        // parcel-local codes are irrelevant here and left at their defaults.
        snapshotBoard.Publish(peer, TestSnapshots.Make(
            seq: snapshotBoard.LastSeq(peer) + 1,
            globalPosition: position,
            realm: realm,
            isTeleport: isTeleport));
    }
}
