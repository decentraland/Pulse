using Decentraland.Pulse;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;
using System.Text;

namespace DCLPulseTests;

[TestFixture]
public class RoomRecoveryTrackerTests
{
    private const string WALLET = "0x1111111111111111111111111111111111111111";
    private const string A = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly PeerIndex peer = new(0);
    private IdentityBoard identities;
    private SnapshotBoard snapshots;
    private RealmSpatialGrids grids;
    private ClusterBoard board;
    private ClusterTracker tracker;
    private NatsPublisher publisher;

    [SetUp]
    public void SetUp()
    {
        identities = new IdentityBoard(4);
        snapshots = new SnapshotBoard(4, 4);
        grids = new RealmSpatialGrids(50, 4);
        board = new ClusterBoard();
        tracker = new ClusterTracker(NullLogger<ClusterTracker>.Instance,
            Options.Create(new ClusterOptions { Enabled = true, DwellPasses = 1 }),
            Options.Create(new NatsOptions { Url = "nats://localhost:4222" }),
            grids, snapshots, identities, board, Substitute.For<IClusterFeedPublisher>(), 4);
        publisher = new NatsPublisher(NullLogger<NatsPublisher>.Instance, NullLoggerFactory.Instance,
            Options.Create(new NatsOptions()), snapshots, board, identities);
        identities.Set(peer, WALLET, A);
        grids.Set(peer, "realm", Vector3.Zero);
        snapshots.SetActive(peer);
        snapshots.Publish(peer, new PeerSnapshot { GlobalPosition = Vector3.Zero, Realm = "realm" });
        // Other members preserve the sticky room across a replacement of the wallet's slot.
        for (uint slot = 1; slot < 3; slot++)
        {
            var anchor = new PeerIndex(slot);
            identities.Set(anchor, $"anchor-{slot}");
            grids.Set(anchor, "realm", Vector3.Zero);
            snapshots.SetActive(anchor);
            snapshots.Publish(anchor, new PeerSnapshot { GlobalPosition = Vector3.Zero, Realm = "realm" });
        }
        tracker.RunPass();
    }

    [TearDown]
    public void TearDown()
    {
        publisher.Dispose();
        tracker.Dispose();
    }

    [Test]
    public void FirstAssignment_IsReadyWithoutBootstrapAndRoomIdsRemainBootUnique()
    {
        PeerClusterChange response = Resolve(A);
        Assert.That(response.HasTokenNotBefore, Is.True);
        Assert.That(response.TokenNotBefore, Is.Zero);
        Assert.That(response.PendingTakeovers, Is.Empty);
        Assert.That(response.ClusterId, Does.Match(@"C[0-9a-f]{32}-"));
    }

    [Test]
    public void LiveRegistrationChange_FencesStaleLookupUntilNextPass()
    {
        identities.Set(peer, WALLET, A);
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(A), out _), Is.False);
        tracker.RunPass();
        Assert.That(Resolve(A).PendingTakeovers, Is.Empty);
        identities.Set(peer, WALLET, B);
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(A), out _), Is.False);
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(B), out _), Is.False);
        tracker.RunPass();
        Assert.That(Resolve(B).PendingTakeovers[0].DisplacedSession, Is.EqualTo(A));
    }

    [Test]
    public void RecycledSlotForAnotherWallet_CannotResolvePreviousAssignment()
    {
        const string other = "0x2222222222222222222222222222222222222222";
        identities.Set(peer, other, A);
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(A), out _), Is.False);
        tracker.RunPass();
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(A), out _), Is.False);
        Assert.That(publisher.TryResolveAssignment($"peer.{other}.cluster_assignment", Encoding.UTF8.GetBytes(A), out PeerClusterChange? response), Is.True);
        Assert.That(response?.PendingTakeovers, Is.Empty);
    }
    [Test]
    public void QueuedCompletionAndNewWinner_ApplyAgainstRetainedTakeoverId()
    {
        identities.Set(peer, WALLET, B); tracker.RunPass();
        PeerClusterChange pending = Resolve(B);
        QueueCompletion(pending, 101);
        identities.Set(peer, WALLET, A); tracker.RunPass();
        PeerClusterChange ready = Resolve(A);
        Assert.That(ready.PendingTakeovers, Is.Empty);
        Assert.That(ready.TokenNotBefore, Is.EqualTo(101));
        identities.Set(peer, WALLET, B); tracker.RunPass();
        PeerClusterChange newer = Resolve(B);
        Assert.That(newer.PendingTakeovers[0].TakeoverId, Is.Not.EqualTo(pending.PendingTakeovers[0].TakeoverId));
        QueueCompletion(pending, 999); tracker.RunPass();
        Assert.That(Resolve(B).PendingTakeovers, Has.Count.EqualTo(1));
    }

    [Test]
    public void Departure_PendingCleanupSurvivesReturnAndQueuedCompletion()
    {
        identities.Set(peer, WALLET, B); tracker.RunPass();
        PeerClusterChange pending = Resolve(B);
        grids.Remove(peer); snapshots.ClearActive(peer); identities.Remove(peer); tracker.RunPass();
        Assert.That(Resolve(B).PendingTakeovers, Is.EqualTo(pending.PendingTakeovers));
        identities.Set(peer, WALLET, A); grids.Set(peer, "realm", Vector3.Zero); snapshots.SetActive(peer); tracker.RunPass();
        Assert.That(Resolve(A).PendingTakeovers, Is.EqualTo(pending.PendingTakeovers));
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(B), out _), Is.False);
        QueueCompletion(pending, 101); tracker.RunPass();
        Assert.That(Resolve(A).PendingTakeovers, Is.Empty);
        Assert.That(Resolve(A).TokenNotBefore, Is.EqualTo(101));
    }

    [Test]
    public void Departure_CompletedCleanupNeverReturnsReadyAssignment()
    {
        identities.Set(peer, WALLET, B); tracker.RunPass();
        PeerClusterChange pending = Resolve(B);
        grids.Remove(peer); snapshots.ClearActive(peer); identities.Remove(peer); tracker.RunPass();
        QueueCompletion(pending, 101); tracker.RunPass();
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(B), out _), Is.False);
        Assert.That(board.RoomRecoveryAssignments[WALLET].PendingTakeovers, Is.Empty);
        Assert.That(board.RoomRecoveryAssignments[WALLET].Active, Is.False);
    }

    [Test]
    public void Confirmation_RequiresValidWalletIdAndPositiveSafeCutoff()
    {
        var completion = new TakeoverCompleted { TakeoverId = "id", RevokeBefore = 101 };
        Assert.That(publisher.TryAcceptTakeoverConfirmation($"peer.{WALLET}.takeover_completed", completion.ToByteArray()), Is.True);
        Assert.That(publisher.TryAcceptTakeoverConfirmation("peer.invalid.takeover_completed", completion.ToByteArray()), Is.False);
        completion.TakeoverId = "";
        Assert.That(publisher.TryAcceptTakeoverConfirmation($"peer.{WALLET}.takeover_completed", completion.ToByteArray()), Is.False);
        completion.TakeoverId = "id"; completion.RevokeBefore = 0;
        Assert.That(publisher.TryAcceptTakeoverConfirmation($"peer.{WALLET}.takeover_completed", completion.ToByteArray()), Is.False);
        completion.RevokeBefore = RoomRecoveryLedger.MAX_SAFE_CUTOFF + 1;
        Assert.That(publisher.TryAcceptTakeoverConfirmation($"peer.{WALLET}.takeover_completed", completion.ToByteArray()), Is.False);
    }

    private PeerClusterChange Resolve(string session)
    {
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(session),
            out PeerClusterChange? response), Is.True);
        return response ?? throw new AssertionException("Expected positive assignment authority.");
    }

    private void QueueCompletion(PeerClusterChange response, ulong cutoff) => Assert.That(
        publisher.TryAcceptTakeoverConfirmation($"peer.{WALLET}.takeover_completed", new TakeoverCompleted
        {
            TakeoverId = response.PendingTakeovers[0].TakeoverId,
            RevokeBefore = cutoff,
        }.ToByteArray()), Is.True);
}
