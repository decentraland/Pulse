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
    public void ProcessEpoch_ScopesClusterIdsAndIsAvailableBeforeBootstrap()
    {
        RoomRecoveryStatus status = board.RoomRecoveryStatus;
        Assert.Multiple(() =>
        {
            Assert.That(status.Epoch, Has.Length.EqualTo(32));
            Assert.That(status.BootstrapRequired, Is.True);
            Assert.That(status.PendingOperations, Is.Zero);
            Assert.That(status.RetainedWallets, Is.EqualTo(3));
            Assert.That(board.Assignments[WALLET].ClusterId, Does.Contain(status.Epoch));
        });
    }

    [Test]
    public void Lookup_ContainsFullPendingPlanAndCannotBypassBootstrap()
    {
        PeerClusterChange response = Resolve(A);
        Assert.Multiple(() =>
        {
            Assert.That(response.RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Pending));
            Assert.That(response.RoomRecovery.BootstrapRequired, Is.True);
            Assert.That(response.RoomRecovery.Operations, Is.Empty);
        });
        Complete(response);
        tracker.RunPass();
        Assert.That(Resolve(A).RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Pending));
        Bootstrap(response.RoomRecovery.Epoch);
        tracker.RunPass();
        Assert.That(Resolve(A).RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Ready));
    }

    [TestCase(A)]
    [TestCase(B)]
    public void Lookup_RegistrationLagDefersAuthorityEvenWhenSessionRepeats(string replacement)
    {
        PeerClusterChange first = Resolve(A);
        identities.Set(peer, WALLET, replacement);
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(A), out _), Is.False);
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(replacement), out _), Is.False);
        tracker.RunPass();
        PeerClusterChange adopted = Resolve(replacement);
        Assert.That(adopted.RoomRecovery.Revision, replacement == A
            ? Is.EqualTo(first.RoomRecovery.Revision) : Is.Not.EqualTo(first.RoomRecovery.Revision));
    }

    [Test]
    public void TakeoverBeforeQueuedCompletion_IsReconciledBeforeAcknowledgement()
    {
        Bootstrap(board.RoomRecoveryStatus.Epoch);
        tracker.RunPass();
        identities.Set(peer, WALLET, B);
        tracker.RunPass();
        PeerClusterChange first = Resolve(B);
        Complete(first);
        identities.Set(peer, WALLET, A);
        tracker.RunPass();
        PeerClusterChange replacement = Resolve(A);
        Assert.Multiple(() =>
        {
            Assert.That(replacement.RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Pending));
            Assert.That(replacement.RoomRecovery.Operations, Has.Count.EqualTo(1));
            Assert.That(replacement.RoomRecovery.Operations[0].OperationId, Is.EqualTo(first.RoomRecovery.Operations[0].OperationId));
        });
    }

    [Test]
    public void DepartedTombstone_IsExplicitAndRejectsAuthorityDuringAnUnadoptedReconnect()
    {
        Bootstrap(board.RoomRecoveryStatus.Epoch);
        tracker.RunPass();
        PeerClusterChange first = Resolve(A);
        grids.Remove(peer);
        snapshots.ClearActive(peer);
        identities.Remove(peer);
        tracker.RunPass();
        PeerClusterChange tombstone = Resolve(A);
        Assert.Multiple(() =>
        {
            Assert.That(tombstone.RoomRecovery.CleanupOnly, Is.True);
            Assert.That(tombstone.ClusterId, Is.Empty);
            Assert.That(tombstone.Realm, Is.Empty);
            Assert.That(tombstone.Session, Is.EqualTo(A));
            Assert.That(tombstone.RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Pending));
            Assert.That(tombstone.RoomRecovery.Operations[0].ClusterId, Is.EqualTo(first.ClusterId));
        });
        identities.Set(peer, WALLET, A);
        Assert.That(publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(A), out _), Is.False);
    }

    [Test]
    public void MalformedOrOversizedConfirmations_DoNotEnterTheInbox()
    {
        Assert.Multiple(() =>
        {
            Assert.That(publisher.TryAcceptCleanupConfirmation($"peer.{WALLET}.room_cleanup_completed", [255]), Is.False);
            Assert.That(publisher.TryAcceptCleanupConfirmation($"peer.{WALLET}.room_cleanup_completed", new byte[4097]), Is.False);
            Assert.That(publisher.TryAcceptBootstrapConfirmation([255]), Is.False);
            Assert.That(publisher.TryAcceptBootstrapConfirmation(new byte[129]), Is.False);
        });
    }

    [Test]
    public void BootstrapConfirmation_IsExactEpochAndIdempotent()
    {
        string epoch = board.RoomRecoveryStatus.Epoch;
        Bootstrap("previous-boot");
        tracker.RunPass();
        Assert.That(board.RoomRecoveryStatus.BootstrapRequired, Is.True);
        Bootstrap(epoch);
        Bootstrap(epoch);
        tracker.RunPass();
        Assert.That(board.RoomRecoveryStatus.BootstrapRequired, Is.False);
        Assert.That(Resolve(A).RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Ready));
    }

    [Test]
    public void FullConfirmationInbox_DefersCleanupAndAllowsRetryAfterTheNextPass()
    {
        Bootstrap(board.RoomRecoveryStatus.Epoch);
        tracker.RunPass();
        identities.Set(peer, WALLET, B);
        tracker.RunPass();
        PeerClusterChange pending = Resolve(B);
        byte[] bootstrap = new RoomRecoveryBootstrapCompleted { Epoch = pending.RoomRecovery.Epoch }.ToByteArray();
        for (var confirmation = 0; confirmation < RoomRecoveryInbox.MAX_CONFIRMATIONS_PER_PASS; confirmation++)
            Assert.That(publisher.TryAcceptBootstrapConfirmation(bootstrap), Is.True);
        Assert.That(publisher.TryAcceptBootstrapConfirmation(bootstrap), Is.False);
        tracker.RunPass();
        Assert.That(Resolve(B).RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Pending));
        Complete(pending);
        tracker.RunPass();
        Assert.That(Resolve(B).RoomRecovery.Admission, Is.EqualTo(RoomAdmissionState.Ready));
    }

    [Test]
    public void ReadyObservation_RequiresTheCanonicalEmptyOperationEncoding()
    {
        var observed = new RoomCleanupCompleted
        {
            Epoch = board.RoomRecoveryStatus.Epoch,
            Revision = "1",
            ObservedReady = true,
        };
        Assert.That(publisher.TryAcceptCleanupConfirmation($"peer.{WALLET}.room_cleanup_completed", observed.ToByteArray()), Is.True);
        observed.OperationId = "not-empty";
        Assert.That(publisher.TryAcceptCleanupConfirmation($"peer.{WALLET}.room_cleanup_completed", observed.ToByteArray()), Is.False);
        observed.OperationId = "";
        observed.ClusterId = "not-empty";
        Assert.That(publisher.TryAcceptCleanupConfirmation($"peer.{WALLET}.room_cleanup_completed", observed.ToByteArray()), Is.False);
        observed.ClusterId = "";
        observed.RevokeBefore = 1;
        Assert.That(publisher.TryAcceptCleanupConfirmation($"peer.{WALLET}.room_cleanup_completed", observed.ToByteArray()), Is.False);
    }

    private PeerClusterChange Resolve(string session)
    {
        bool found = publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(session),
            out PeerClusterChange? response);
        Assert.That(found, Is.True);
        return response ?? throw new AssertionException("Expected positive room authority.");
    }

    private void Bootstrap(string epoch) => Assert.That(publisher.TryAcceptBootstrapConfirmation(
        new RoomRecoveryBootstrapCompleted { Epoch = epoch }.ToByteArray()), Is.True);

    private void Complete(PeerClusterChange response)
    {
        foreach (RoomCleanupOperation operation in response.RoomRecovery.Operations)
            Assert.That(publisher.TryAcceptCleanupConfirmation($"peer.{WALLET}.room_cleanup_completed",
                new RoomCleanupCompleted
                {
                    Epoch = response.RoomRecovery.Epoch,
                    Revision = response.RoomRecovery.Revision,
                    OperationId = operation.OperationId,
                    ClusterId = operation.ClusterId,
                    RevokeBefore = operation.MinimumRevokeBefore,
                }.ToByteArray()), Is.True);
    }
}
