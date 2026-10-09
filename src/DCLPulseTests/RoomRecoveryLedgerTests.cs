using Pulse.Clusters;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

[TestFixture]
public class RoomRecoveryLedgerTests
{
    private const string WALLET = "wallet";
    private const ulong NOW = 1000;
    private RoomRecoveryLedger ledger;
    private IdentityBoard identities;

    [SetUp]
    public void SetUp()
    {
        ledger = new RoomRecoveryLedger(new ClusterOptions(), "boot");
        identities = new IdentityBoard(4);
    }

    [Test]
    public void StartupAndCleanup_BothRequireConfirmationBeforeAdmission()
    {
        Desired("A", "R1");
        RoomRecoveryAssignment first = Plan();
        Assert.That(first.Operations, Is.Empty, "a new boot-scoped room has no previously admitted holder");
        Assert.That(Plan().Ready, Is.False);
        ledger.Apply(new RoomRecoveryConfirmation("", "wrong-boot", "", "", "", 0, true), NOW);
        Assert.That(Plan().BootstrapRequired, Is.True);
        Bootstrap();
        Assert.That(Plan().Ready, Is.True);
        Desired("B", "R1");
        Assert.That(Plan().Ready, Is.False);
        CompleteAll();
        Assert.That(Plan().Ready, Is.True);
    }

    [Test]
    public void SameSessionReconnect_KeepsRevisionReadinessAndCutoff()
    {
        Admit("A", "R1");
        RoomRecoveryAssignment first = Plan();
        Desired("A", "R1", slot: 1);
        RoomRecoveryAssignment reconnect = Plan();
        Assert.Multiple(() =>
        {
            Assert.That(reconnect.Revision, Is.EqualTo(first.Revision));
            Assert.That(reconnect.Ready, Is.True);
            Assert.That(reconnect.TokenNotBefore, Is.EqualTo(first.TokenNotBefore));
            Assert.That(reconnect.Identity, Is.Not.SameAs(first.Identity));
            Assert.That(reconnect.Operations, Is.Empty);
        });
    }

    [Test]
    public void RapidTakeovers_CarryUnfinishedRoomsAndRejectOldRevision()
    {
        Admit("A", "R1");
        RoomRecoveryAssignment a = Plan();
        Desired("B", "R2");
        RoomRecoveryAssignment b = Plan();
        Desired("C", "R3");
        RoomRecoveryAssignment c = Plan();
        Complete(b, b.Operations[0]);
        Assert.That(Plan().Operations, Has.Count.EqualTo(1));
        Assert.That(c.Operations[0].OperationId, Is.EqualTo(b.Operations[0].OperationId));
        Assert.That(c.Operations[0].ClusterId, Is.EqualTo(a.Assignment.ClusterId));
        Bootstrap();
        CompleteAll();
        Assert.That(Plan().Ready, Is.True);
    }

    [Test]
    public void ReturnToPreviouslyAdmittedSession_CreatesFreshSameRoomOperation()
    {
        Admit("A", "R1");
        RoomRecoveryAssignment a = Plan();
        Desired("B", "R1");
        RoomRecoveryAssignment b = Plan();
        Assert.That(b.Operations[0].MinimumRevokeBefore, Is.GreaterThan(a.TokenNotBefore));
        CompleteAll();
        Assert.That(Plan().Ready, Is.True);
        Desired("A", "R1");
        RoomRecoveryAssignment returned = Plan();
        Assert.Multiple(() =>
        {
            Assert.That(returned.Ready, Is.False);
            Assert.That(returned.Revision, Is.Not.EqualTo(a.Revision));
            Assert.That(returned.Operations[0].OperationId, Is.Not.EqualTo(b.Operations[0].OperationId));
            Assert.That(returned.Operations[0].MinimumRevokeBefore, Is.GreaterThan(PlanFloor(b)));
        });
        Complete(b, b.Operations[0]);
        Assert.That(Plan().Ready, Is.False);
    }

    [Test]
    public void SameRoomUnadmittedTakeovers_KeepOneStableOperation()
    {
        Admit("A", "R1");
        Desired("B", "R1");
        string operation = Plan().Operations[0].OperationId;
        for (var owner = 0; owner < 100; owner++) Desired($"session-{owner}", "R1");
        Assert.Multiple(() =>
        {
            Assert.That(Plan().Operations, Has.Count.EqualTo(1));
            Assert.That(Plan().Operations[0].OperationId, Is.EqualTo(operation));
        });
    }

    [Test]
    public void MovementBackToRetiringRoom_RemainsPendingAndPreservesItsOperation()
    {
        Admit("A", "R1");
        Desired("A", "R2");
        RoomRecoveryAssignment away = Plan();
        PendingRoomCleanup retirement = away.Operations.Single(operation => operation.ClusterId == "R1");
        Desired("A", "R1");
        Assert.Multiple(() =>
        {
            Assert.That(Plan().Ready, Is.False);
            Assert.That(Plan().Operations.Single(operation => operation.ClusterId == "R1"), Is.EqualTo(retirement));
        });
        Complete(away, retirement);
        Assert.That(Plan().Ready, Is.False);
        CompleteAll();
        Assert.That(Plan().Ready, Is.True);
        Assert.That(Plan().TokenNotBefore, Is.EqualTo(retirement.MinimumRevokeBefore));
    }

    [Test]
    public void Departure_RetainsCleanupWithoutATtlThenPrunesConfirmedTombstone()
    {
        Admit("A", "R1");
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        RoomRecoveryAssignment departed = Plan();
        for (var pass = 0; pass < 1000; pass++) ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        Assert.Multiple(() =>
        {
            Assert.That(Plan().CleanupOnly, Is.True);
            Assert.That(Plan().Ready, Is.False);
            Assert.That(Plan().Assignment.Session, Is.EqualTo("A"));
            Assert.That(Plan().Operations, Is.EqualTo(departed.Operations));
        });
        CompleteAll();
        Assert.That(Plan().CleanupOnly, Is.True);
        Assert.That(Plan().Ready, Is.True);
        Observe(Plan());
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(),
            Plan().TokenNotBefore + RoomRecoveryLedger.RETIREMENT_CLOCK_GRACE_SECONDS + 1);
        Assert.That(ledger.Snapshot(out _), Is.Empty);
    }

    [TestCase("epoch")]
    [TestCase("revision")]
    [TestCase("operation")]
    [TestCase("room")]
    [TestCase("minimum")]
    [TestCase("future")]
    public void Completion_WithInvalidAuthorityOrBoundary_DoesNotClearOperation(string mismatch)
    {
        Admit("A", "R1");
        Desired("B", "R1");
        RoomRecoveryAssignment plan = Plan();
        PendingRoomCleanup operation = plan.Operations[0];
        var completion = Confirmation(plan, operation);
        completion = mismatch switch
        {
            "epoch" => completion with { Epoch = "old" },
            "revision" => completion with { Revision = "999" },
            "operation" => completion with { OperationId = "old-operation" },
            "room" => completion with { ClusterId = "other" },
            "minimum" => completion with { RevokeBefore = operation.MinimumRevokeBefore - 1 },
            _ => completion with { RevokeBefore = NOW + 61 },
        };
        ledger.Apply(completion, NOW);
        Assert.That(Plan().Operations, Has.Count.EqualTo(1));
    }

    [Test]
    public void OldConfirmedCutoff_AfterAnOutage_RemainsAcceptable()
    {
        Admit("A", "R1");
        Desired("B", "R1");
        RoomRecoveryAssignment plan = Plan();
        ledger.Apply(Confirmation(plan, plan.Operations[0]), NOW + 86400);
        Assert.That(Plan().Operations, Is.Empty);
    }

    [Test]
    public void ReturnAfterConfirmedRetirement_UsesAFloorAboveTheEarlierRoomCredentials()
    {
        Admit("A", "R1");
        Desired("A", "R2");
        CompleteAll();
        ulong previousFloor = Plan().TokenNotBefore;
        Desired("A", "R1");
        Assert.That(Plan().Operations.Single(operation => operation.ClusterId == "R2").MinimumRevokeBefore,
            Is.GreaterThan(previousFloor));
    }

    [Test]
    public void RoomCapacity_RapidMovesCarryOnlyThePreviouslyAuthorizedRoom()
    {
        ledger = new RoomRecoveryLedger(new ClusterOptions { MaxRecoveryRoomsPerWallet = 1 }, "boot");
        Admit("A", "R1");
        Desired("A", "R2");
        RoomRecoveryAssignment blocked = Plan();
        Assert.That(blocked.Ready, Is.False);
        Assert.That(blocked.Operations[0].ClusterId, Is.EqualTo("R1"));
        Desired("A", "R3");
        Assert.That(Plan().Operations, Is.EqualTo(blocked.Operations));
        CompleteAll();
        Assert.That(Plan().Ready, Is.True);
    }

    [Test]
    public void ZeroRoomCapacity_BlocksRetirementWithoutForgettingTheAdmittedRoom()
    {
        ledger = new RoomRecoveryLedger(new ClusterOptions { MaxRecoveryRoomsPerWallet = 0 }, "boot");
        Admit("A", "R1");
        Desired("A", "R2");
        Assert.That(ledger.CapacityBlocked, Is.True);
        Assert.That(Plan().Ready, Is.False);
        Desired("B", "R3");
        Assert.That(ledger.CapacityBlocked, Is.True);
        Assert.That(Plan().Ready, Is.False);
    }

    [Test]
    public void WalletCapacity_DoesNotEvictDepartedPendingWork()
    {
        ledger = new RoomRecoveryLedger(new ClusterOptions { MaxRecoveryWallets = 1 }, "boot");
        Admit("A", "R1");
        Desired("B", "R2", wallet: "other");
        IReadOnlyDictionary<string, RoomRecoveryAssignment> plans = ledger.Snapshot(out _);
        Assert.Multiple(() =>
        {
            Assert.That(ledger.CapacityBlocked, Is.True);
            Assert.That(plans.ContainsKey(WALLET), Is.True);
            Assert.That(plans.ContainsKey("other"), Is.False);
            Assert.That(plans[WALLET].CleanupOnly, Is.True);
        });
        CompleteAll();
        Observe(Plan());
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + RoomRecoveryLedger.RETIREMENT_CLOCK_GRACE_SECONDS + 1);
        ledger.Snapshot(out _);
        Desired("B", "R2", wallet: "other", now: NOW + RoomRecoveryLedger.RETIREMENT_CLOCK_GRACE_SECONDS + 1);
        Assert.That(ledger.Snapshot(out _).ContainsKey("other"), Is.True);
    }

    [Test]
    public void PublishedSnapshot_DoesNotChangeWhenTheLedgerAdvances()
    {
        Admit("A", "R1");
        Desired("B", "R1");
        RoomRecoveryAssignment previous = Plan();
        CompleteAll();
        Bootstrap();
        Assert.That(Plan().Ready, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(previous.Ready, Is.False);
            Assert.That(previous.BootstrapRequired, Is.False);
            Assert.That(previous.Operations, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void CompletedTombstone_RemainsUntilItsReadyReceiptIsObserved()
    {
        Admit("A", "R1");
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        CompleteAll();
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 86400);
        Assert.That(Plan().Ready, Is.True);
        Assert.That(Plan().CleanupOnly, Is.True);
        Observe(Plan(), NOW + 86400);
        Assert.That(ledger.Snapshot(out _), Is.Empty);
    }

    [Test]
    public void FutureCutoff_ObservedTombstoneRetainsItsFloorUntilTheClockPassesIt()
    {
        Admit("A", "R1");
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        RoomRecoveryAssignment departed = Plan();
        ledger.Apply(Confirmation(departed, departed.Operations[0]) with { RevokeBefore = NOW + 50 }, NOW);
        RoomRecoveryAssignment ready = Plan();
        Observe(ready);
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 50);
        Assert.That(Plan().TokenNotBefore, Is.EqualTo(NOW + 50));
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 51);
        Assert.That(Plan().TokenNotBefore, Is.EqualTo(NOW + 50));
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 50 + RoomRecoveryLedger.RETIREMENT_CLOCK_GRACE_SECONDS);
        Assert.That(Plan().TokenNotBefore, Is.EqualTo(NOW + 50));
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 51 + RoomRecoveryLedger.RETIREMENT_CLOCK_GRACE_SECONDS);
        Assert.That(ledger.Snapshot(out _), Is.Empty);
    }

    [Test]
    public void ReturningWalletAfterCutoffWithinClockGrace_InheritsTheFloorForBackdatedCredentials()
    {
        Admit("A", "R1");
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        CompleteAll();
        RoomRecoveryAssignment retired = Plan();
        Observe(retired);
        ulong returnTime = retired.TokenNotBefore + 2;
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), returnTime);
        Assert.That(Plan().CleanupOnly, Is.True);
        Desired("A", "R1", now: returnTime);
        Assert.Multiple(() =>
        {
            Assert.That(Plan().Ready, Is.True);
            Assert.That(Plan().TokenNotBefore, Is.EqualTo(retired.TokenNotBefore));
            Assert.That(Plan().TokenNotBefore, Is.GreaterThan(returnTime - 5));
        });
    }

    [Test]
    public void ReturningWalletBeforeFutureCutoffExpiry_InheritsTheConfirmedFloor()
    {
        Admit("A", "R1");
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        RoomRecoveryAssignment departed = Plan();
        ledger.Apply(Confirmation(departed, departed.Operations[0]) with { RevokeBefore = NOW + 50 }, NOW);
        RoomRecoveryAssignment ready = Plan();
        Observe(ready);
        Desired("A", "R1");
        Assert.Multiple(() =>
        {
            Assert.That(Plan().Ready, Is.True);
            Assert.That(Plan().TokenNotBefore, Is.EqualTo(NOW + 50));
            Assert.That(Plan().CleanupOnly, Is.False);
        });
        Observe(ready, NOW + 60);
        Assert.That(Plan().CleanupOnly, Is.False, "the stale observation cannot retire the returning owner");
    }

    [Test]
    public void ReadyObservation_CannotClearPendingCleanupOrBypassBootstrap()
    {
        Desired("A", "R1");
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        Observe(Plan());
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 1);
        Assert.That(Plan().BootstrapRequired, Is.True);
        Bootstrap();
        Observe(Plan());
        Assert.That(ledger.Snapshot(out _), Is.Empty);
        Admit("A", "R1");
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW);
        RoomRecoveryAssignment pending = Plan();
        Observe(pending, NOW + 10);
        Assert.That(Plan().Operations, Has.Count.EqualTo(1));
        CompleteAll();
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>(), NOW + 10);
        Assert.That(Plan().CleanupOnly, Is.True, "observing a pending record cannot authorize later pruning");
    }

    [Test]
    public void ConfirmationInbox_RefusesOverflowWithoutReplacingQueuedWork()
    {
        var inbox = new RoomRecoveryInbox(1);
        var first = new RoomRecoveryConfirmation(WALLET, "boot", "1", "one", "R1", NOW);
        Assert.That(inbox.TryWrite(first), Is.True);
        Assert.That(inbox.TryWrite(first with { OperationId = "two" }), Is.False);
        Assert.That(inbox.TryRead(out RoomRecoveryConfirmation retained), Is.True);
        Assert.That(retained, Is.EqualTo(first));
    }

    private void Desired(string session, string room, uint slot = 0, string wallet = WALLET, ulong now = NOW)
    {
        var peer = new PeerIndex(slot);
        identities.Set(peer, wallet, session);
        IdentityRegistration? identity = identities.GetIdentity(peer);
        Assert.That(identity, Is.Not.Null);
        if (identity is null) return;
        ledger.Reconcile(new Dictionary<string, DesiredRoomAssignment>
        {
            [wallet] = new(new ClusterAssignment(room, "realm", session), peer, identity),
        }, now);
    }

    private void Admit(string session, string room)
    {
        Desired(session, room);
        Bootstrap();
        CompleteAll();
        Assert.That(Plan().Ready, Is.True);
    }

    private void Bootstrap() => ledger.Apply(new RoomRecoveryConfirmation("", "boot", "", "", "", 0, true), NOW);

    private void Observe(RoomRecoveryAssignment plan, ulong now = NOW) => ledger.Apply(
        new RoomRecoveryConfirmation(WALLET, plan.Epoch, plan.Revision, "", "", 0, ObservedReady: true), now);

    private RoomRecoveryAssignment Plan() => ledger.Snapshot(out _)[WALLET];

    private static ulong PlanFloor(RoomRecoveryAssignment plan) => plan.Operations[0].MinimumRevokeBefore;

    private void CompleteAll()
    {
        RoomRecoveryAssignment plan = Plan();
        foreach (PendingRoomCleanup operation in plan.Operations) Complete(plan, operation);
    }

    private void Complete(RoomRecoveryAssignment plan, PendingRoomCleanup operation) => ledger.Apply(Confirmation(plan, operation), NOW);

    private static RoomRecoveryConfirmation Confirmation(RoomRecoveryAssignment plan, PendingRoomCleanup operation) =>
        new(WALLET, plan.Epoch, plan.Revision, operation.OperationId, operation.ClusterId, operation.MinimumRevokeBefore);
}
