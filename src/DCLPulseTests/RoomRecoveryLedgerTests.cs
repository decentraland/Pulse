using Decentraland.Pulse;
using Pulse.Clusters;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

[TestFixture]
public class RoomRecoveryLedgerTests
{
    private const ulong NOW = 100;
    private RoomRecoveryLedger ledger;
    private readonly IdentityBoard identities = new(4);

    [SetUp]
    public void SetUp()
    {
        identities.Set(new PeerIndex(0), "wallet", "A");
        ledger = new RoomRecoveryLedger(new ClusterOptions { SessionRetentionPasses = 2 }, "boot");
    }

    [Test]
    public void FirstAssignment_AdvertisesCapabilityWithExplicitZero()
    {
        Desired("A");
        var message = new PeerClusterChange();
        RoomRecoveryLedger.Stamp(message, Plan());
        Assert.That(message.HasTokenNotBefore, Is.True);
        Assert.That(message.TokenNotBefore, Is.Zero);
        Assert.That(message.PendingTakeovers, Is.Empty);
    }

    [Test]
    public void LostHintsAndCompletions_KeepStablePendingUntilMatchingCompletion()
    {
        Desired("A"); Plan(); Desired("B");
        RoomRecoveryAssignment pending = Plan();
        Desired("B", now: NOW + 1000);
        Assert.That(Plan().PendingTakeovers, Is.EqualTo(pending.PendingTakeovers));
        ledger.Apply(new RoomRecoveryConfirmation("other", pending.PendingTakeovers[0].TakeoverId, 101), NOW);
        ledger.Apply(new RoomRecoveryConfirmation("wallet", "stale", 101), NOW);
        Assert.That(Plan().PendingTakeovers, Has.Count.EqualTo(1));
        Complete(pending, 101);
        Assert.That(Plan().PendingTakeovers, Is.Empty);
        Assert.That(pending.PendingTakeovers, Has.Count.EqualTo(1), "published snapshots are immutable");
    }

    [Test]
    public void UnadmittedChainAndTemporaryDeparture_PreserveOriginalDisplacement()
    {
        Desired("A"); Plan(); Desired("B", "room-b");
        RoomRecoveryAssignment b = Plan();
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 1000);
        Desired("C", "room-c", NOW + 2000);
        Assert.That(Plan().PendingTakeovers, Is.EqualTo(b.PendingTakeovers));
        Assert.That(Plan().PendingTakeovers[0].DisplacedSession, Is.EqualTo("A"));
        Complete(b, 101);
        Assert.That(Plan().PendingTakeovers, Is.Empty);
        Assert.That(Plan().Assignment.Session, Is.EqualTo("C"));
    }

    [Test]
    public void SameRoomAba_UsesFreshIdAndStaleCompletionCannotClearNewWork()
    {
        Desired("A"); Plan(); Desired("B");
        RoomRecoveryAssignment first = Plan(); Complete(first, 101); Plan();
        Desired("A"); RoomRecoveryAssignment second = Plan();
        Assert.That(second.PendingTakeovers[0].TakeoverId, Is.Not.EqualTo(first.PendingTakeovers[0].TakeoverId));
        Complete(first, 999);
        Assert.That(Plan().PendingTakeovers, Has.Count.EqualTo(1));
        Complete(second, 101);
        Assert.That(Plan().PendingTakeovers, Has.Count.EqualTo(1), "cutoff must exceed prior credential floor");
        Complete(second, 102);
        Assert.That(Plan().TokenNotBefore, Is.EqualTo(102));
    }

    [Test]
    public void Completion_RejectsZeroUnsafeAndPriorFloorAndDuplicateCannotRaiseFloor()
    {
        Desired("A"); Plan(); Desired("B"); RoomRecoveryAssignment pending = Plan();
        Complete(pending, 0); Complete(pending, RoomRecoveryLedger.MAX_SAFE_CUTOFF + 1);
        Assert.That(Plan().PendingTakeovers, Has.Count.EqualTo(1));
        Complete(pending, 101); Complete(pending, 999);
        Assert.That(Plan().TokenNotBefore, Is.EqualTo(101));
    }

    [Test]
    public void OrdinaryMovesAndDepartures_DoNotCreateTakeoverWork()
    {
        Desired("A"); Plan(); Desired("a", "room-b");
        Assert.That(Plan().PendingTakeovers, Is.Empty);
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 1);
        Assert.That(Plan().Active, Is.False);
        Assert.That(Plan().PendingTakeovers, Is.Empty);
    }

    [Test]
    public void ZeroTakeoverCapacity_FailsClosedAndRetainsUnrecordedDisplacementBeyondTtl()
    {
        ledger = new RoomRecoveryLedger(new ClusterOptions { MaxRecoveryRoomsPerWallet = 0, SessionRetentionPasses = 1 });
        Desired("A"); Plan(); Desired("B");
        var message = new PeerClusterChange(); RoomRecoveryLedger.Stamp(message, Plan());
        Assert.That(message.HasTokenNotBefore, Is.False);
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 1000);
        Desired("C", now: NOW + 2000);
        Assert.That(Plan().CapacityBlocked, Is.True);
        Assert.That(Plan().PendingTakeovers, Is.Empty);
    }

    [Test]
    public void WalletCapacity_RetainsUnfinishedWorkAndReturnsUnsupportedOverflow()
    {
        ledger = new RoomRecoveryLedger(new ClusterOptions { MaxRecoveryWallets = 1, SessionRetentionPasses = 1 });
        Desired("A"); Plan(); Desired("B"); RoomRecoveryAssignment pending = Plan();
        var desired = new Dictionary<string, DesiredRoomAssignment> { ["other"] = Target("X", "r") };
        ledger.Reconcile(desired, NOW + 1000);
        IReadOnlyDictionary<string, RoomRecoveryAssignment> snapshot = ledger.Snapshot(out RoomRecoveryStatus status);
        Assert.That(snapshot["wallet"].PendingTakeovers, Is.EqualTo(pending.PendingTakeovers));
        Assert.That(snapshot["other"].CapacityBlocked, Is.True);
        Assert.That(status.RetainedWallets, Is.EqualTo(1));
    }

    [Test]
    public void CompletedFloor_RetainedUntilCutoffAndClockGraceHavePassed()
    {
        Desired("A"); Plan(); Desired("B"); Complete(Plan(), NOW + 1000);
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 999);
        Assert.That(Plan().TokenNotBefore, Is.EqualTo(NOW + 1000));
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 1001 + RoomRecoveryLedger.RETIREMENT_CLOCK_GRACE_SECONDS);
        Assert.That(ledger.Snapshot(out _), Is.Empty);
    }

    [Test]
    public void Inbox_IsBoundedAndRetainsAcceptedConfirmation()
    {
        var inbox = new RoomRecoveryInbox(1);
        var first = new RoomRecoveryConfirmation("wallet", "one", 101);
        Assert.That(inbox.TryWrite(first), Is.True);
        Assert.That(inbox.TryWrite(new RoomRecoveryConfirmation("wallet", "two", 102)), Is.False);
        Assert.That(inbox.TryRead(out RoomRecoveryConfirmation actual), Is.True);
        Assert.That(actual, Is.EqualTo(first));
    }

    private void Desired(string session, string room = "room", ulong now = NOW) =>
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment> { ["wallet"] = Target(session, room) }, now);
    private DesiredRoomAssignment Target(string session, string room) => new(new ClusterAssignment(room, "realm", session),
        new PeerIndex(0), identities.GetIdentity(new PeerIndex(0)) ?? throw new AssertionException("missing identity"));
    private RoomRecoveryAssignment Plan() => ledger.Snapshot(out _)["wallet"];
    private void Complete(RoomRecoveryAssignment plan, ulong cutoff) => ledger.Apply(
        new RoomRecoveryConfirmation("wallet", plan.PendingTakeovers[0].TakeoverId, cutoff), NOW);
}
