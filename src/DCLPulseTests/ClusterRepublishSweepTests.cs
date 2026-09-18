using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse;
using System.Numerics;

namespace DCLPulseTests;

/// <summary>
///     The periodic re-publish sweep: a settled assignment re-emitted on
///     <c>peer.{wallet}.cluster_refresh</c> so a consumer that lost the original event recovers
///     without waiting for the peer to move.
/// </summary>
[TestFixture]
public class ClusterRepublishSweepTests
{
    private const int MAX_PEERS = 32;
    private const int RING_CAPACITY = 4;
    private const float CELL_SIZE = 50f;

    private const string REALM = "realm-a";
    private const string OTHER_REALM = "realm-b";
    private const string WALLET = "0xsettled-peer";
    private const int INTERVAL = 3;

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
    public void RunPass_SweepDisabled_NeverRepublishes()
    {
        ClusterTracker tracker = CreateTracker(republishIntervalPasses: 0);
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), WALLET);

        for (var pass = 0; pass < 10; pass++)
            tracker.RunPass();

        feedPublisher.DidNotReceive()
                     .PublishClusterRefresh(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ClusterSession>());
    }

    [Test]
    public void RunPass_SettledAssignmentInsideTheInterval_DoesNotRepublish()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), WALLET);

        // The publishing pass plus INTERVAL - 1 more: the deadline has not arrived.
        for (var pass = 0; pass < INTERVAL; pass++)
            tracker.RunPass();

        feedPublisher.DidNotReceive()
                     .PublishClusterRefresh(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ClusterSession>());
    }

    [Test]
    public void RunPass_SettledAssignmentPastTheInterval_RepublishesTheSameAssignment()
    {
        ClusterTracker tracker = CreateTracker();
        var peer = new PeerIndex(0);
        SetupPeer(peer, new Vector3(10, 0, 10), WALLET);

        tracker.RunPass();
        string cluster = clusterBoard.Current.GetClusterId(peer)!;
        feedPublisher.ClearReceivedCalls();

        for (var pass = 0; pass < INTERVAL; pass++)
            tracker.RunPass();

        feedPublisher.Received(1).PublishClusterRefresh(WALLET, cluster, REALM, Arg.Any<ClusterSession>());
    }

    [Test]
    public void RunPass_PastTheInterval_RepublishesWithoutEmittingAChange()
    {
        // The refresh must not be mistaken for a reassignment: nothing changed, so the change feed
        // stays silent and only the refresh subject carries it.
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), WALLET);

        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        for (var pass = 0; pass < INTERVAL; pass++)
            tracker.RunPass();

        feedPublisher.DidNotReceive()
                     .PublishClusterChange(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ClusterSession>());
    }

    [Test]
    public void RunPass_AfterARepublish_WaitsAnotherIntervalBeforeRepublishingAgain()
    {
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), WALLET);

        // Two full deadlines: the sweep must fire once per interval, not once per pass.
        for (var pass = 0; pass < 1 + (INTERVAL * 2); pass++)
            tracker.RunPass();

        feedPublisher.Received(2)
                     .PublishClusterRefresh(WALLET, Arg.Any<string>(), REALM, Arg.Any<ClusterSession>());
    }

    [Test]
    public void RunPass_AssignmentChangedBeforeTheDeadline_ResetsTheRepublishClock()
    {
        // A realm change is an unambiguous reassignment: it bypasses the debounce and cannot be
        // absorbed by sticky-ID inheritance the way a move within one realm can.
        ClusterTracker tracker = CreateTracker();
        var peer = new PeerIndex(0);

        SetupPeer(peer, new Vector3(10, 0, 10), WALLET);
        tracker.RunPass();

        MovePeerToRealm(peer, new Vector3(10, 0, 10), WALLET, OTHER_REALM);
        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        // One short of a fresh deadline measured from the change, not from the first publish.
        for (var pass = 0; pass < INTERVAL - 1; pass++)
            tracker.RunPass();

        feedPublisher.DidNotReceive()
                     .PublishClusterRefresh(WALLET, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ClusterSession>());
    }

    [Test]
    public void RunPass_RepublishedAssignment_NamesNoDisplacedSession()
    {
        // A settled peer's retained ledger entry holds its own session, so a refresh carries the
        // session alone. A consumer must not read a refresh as a takeover.
        ClusterTracker tracker = CreateTracker();
        SetupPeer(new PeerIndex(0), new Vector3(10, 0, 10), WALLET);

        tracker.RunPass();
        feedPublisher.ClearReceivedCalls();

        for (var pass = 0; pass < INTERVAL; pass++)
            tracker.RunPass();

        feedPublisher.Received(1)
                     .PublishClusterRefresh(
                          WALLET,
                          Arg.Any<string>(),
                          REALM,
                          Arg.Is<ClusterSession>(s => s.DisplacedSession == null && s.DisplacedClusterId == null));
    }

    private ClusterTracker CreateTracker(int republishIntervalPasses = INTERVAL)
    {
        IOptions<ClusterOptions> options = Options.Create(new ClusterOptions
        {
            Enabled = true,
            PassIntervalMs = 1000,
            DwellPasses = 1,
            IdPrefix = "C",
            RepublishIntervalPasses = republishIntervalPasses,
            SessionRetentionPasses = 300,
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
    ///     Moves a peer to another realm. The grid entry must be vacated first: a peer is only ever
    ///     indexed in one realm's grid, and <c>Set</c> does not migrate it.
    /// </summary>
    private void MovePeerToRealm(PeerIndex peer, Vector3 position, string wallet, string realm)
    {
        grids.Remove(peer);
        grids.Set(peer, realm, position);
        identityBoard.Set(peer, wallet, wallet);

        snapshotBoard.Publish(peer, TestSnapshots.Make(
            seq: snapshotBoard.LastSeq(peer) + 1,
            globalPosition: position,
            realm: realm));
    }

    private void SetupPeer(PeerIndex peer, Vector3 position, string wallet)
    {
        grids.Set(peer, REALM, position);
        snapshotBoard.SetActive(peer);
        identityBoard.Set(peer, wallet, wallet);

        snapshotBoard.Publish(peer, TestSnapshots.Make(
            seq: snapshotBoard.LastSeq(peer) + 1,
            globalPosition: position,
            realm: REALM));
    }
}
