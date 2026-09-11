using Decentraland.Pulse;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Clusters;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Presence;
using Pulse.Transport;

namespace DCLPulseTests;

/// <summary>
///     The five guarantees Pulse holds for <c>engine.parcel_changes</c> (iteration-2 C1), one test per
///     clause, each written so that it fails when the clause is violated rather than when the
///     implementation is merely rearranged. Everything runs through the real pass, the real outbox and
///     the real peer-lifecycle cleanup; the bytes those produce are pinned separately in
///     <see cref="PresenceWireFixtureTests" />.
/// </summary>
[TestFixture]
public class PresenceGuaranteeTests
{
    private const long T0 = IterationTwoFixtures.T0;
    private const string MAIN = "main";
    private const string COZYFARM = "cozyfarm.dcl.eth";

    private static readonly PeerIndex P1 = new (1);
    private static readonly PeerIndex P2 = new (2);
    private static readonly PeerIndex P3 = new (3);
    private static readonly PeerIndex P4 = new (4);

    private static string Wallet(int n) => IterationTwoFixtures.Wallet(n);

    // ── C1.1 first placement ────────────────────────────────────────

    /// <summary>
    ///     A peer that joins after the opening snapshot has no previous state at all, so its first
    ///     placement is a change — from nowhere — and goes out once. Nothing follows while it stands
    ///     still, which is the other half of the guarantee: the entry has to be the placement itself,
    ///     not a side effect of a later move.
    /// </summary>
    [Test]
    public void FirstPlacement_IsPublishedOnce_EvenIfThePeerNeverMoves()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Place(P2, Wallet(2), MAIN, 12, 34);
        scenario.RunPass();

        ParcelChangesBatch batch = scenario.NextBatch(T0 + 2000)!;

        Assert.That(batch.Snapshot, Is.False, "a peer joining is a delta, not a reason to re-send the server");
        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(2)} {MAIN} 12,34" }));

        scenario.RunPass();
        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 4000), Is.Null,
            "standing still is not news — the placement was already published");
    }

    // ── C1.2 exit paths ─────────────────────────────────────────────

    /// <summary>
    ///     The transport's own <c>Disconnected</c> event — a client that closed the connection, or an
    ///     ENet timeout — is the baseline exit: one parcel-absent entry, keeping the realm the peer
    ///     was last in so a consumer can scope the removal.
    /// </summary>
    [Test]
    public void CleanDisconnect_EmitsExactlyOneExitEntry()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Remove(P1);
        scenario.RunPass();

        AssertSingleExit(scenario, Wallet(1), MAIN);
    }

    /// <summary>
    ///     A peer that never authenticated has no presence to withdraw: it was never placed in a
    ///     realm, so it never reached the feed and its removal must stay silent. Publishing an exit
    ///     for it would tell every consumer to remove a wallet it had never been told about — and for
    ///     the duplicate-session case, a wallet that is at that moment online on another connection.
    ///     <para />
    ///     The timeout itself is driven here, not simulated: the real
    ///     <see cref="PeerSimulation.SimulateTick" /> is what disconnects a peer left in
    ///     <c>PENDING_AUTH</c> past <c>Peers:PendingAuthCleanTimeoutMs</c>.
    /// </summary>
    [Test]
    public void PendingAuthTimeout_DisconnectsThePeer_AndAnnouncesNoExit()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.DispatchConnected(P3);

        Assert.That(scenario.Peers[P3].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH));

        scenario.Clock.MonotonicTime += 30_000;
        scenario.Simulation.SimulateTick(scenario.Peers, tickCounter: 1);

        scenario.Transport.Received(1).Disconnect(P3, DisconnectReason.AUTH_TIMEOUT);

        scenario.Remove(P3);
        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 2000), Is.Null,
            "a peer that was never on the feed has no exit to announce");
    }

    /// <summary>
    ///     A second connection for the same wallet evicts the first.
    ///     <c>HandshakeHandlerTests.Handle_CleanupOfEvictedPeer_ThirdHandshakeStillEvictsLiveSession</c>
    ///     pins that the handshake calls <c>transport.Disconnect(evicted, DUPLICATE_SESSION)</c>; what
    ///     this pins is the half that belongs to the feed — the lifecycle event that kick produces
    ///     withdraws the evicted peer's presence exactly once.
    /// </summary>
    [Test]
    public void DuplicateSessionKick_EmitsExactlyOneExitEntry()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Transport.Disconnect(P1, DisconnectReason.DUPLICATE_SESSION);
        scenario.Remove(P1);
        scenario.RunPass();

        AssertSingleExit(scenario, Wallet(1), MAIN);
    }

    /// <summary>
    ///     ...and the case that makes the exit <b>wallet-scoped</b> (A1). The handshake accepts the
    ///     new session at once and the evicted slot is only cleaned
    ///     <c>Peers:DisconnectionCleanTimeoutMs</c> later, so by the time the cleanup runs the wallet
    ///     is already standing somewhere on a newer connection. Consumers key presence by wallet, so
    ///     withdrawing it here takes a peer that is online offline — and if both entries land in one
    ///     batch, per-address coalescing keeps the exit and the new placement is never seen at all.
    ///     <para />
    ///     The newer placement is the whole of this wallet's presence, so the stale slot's departure
    ///     is not news.
    /// </summary>
    [Test]
    public void DuplicateSessionKick_ForAWalletAlreadyPlacedOnANewerSlot_EmitsNoExit()
    {
        PresenceScenario scenario = OpenedScenario();

        // The second client instance handshakes: the existing session is kicked and the new one is
        // placed on its own slot, for the same wallet.
        scenario.Transport.Disconnect(P1, DisconnectReason.DUPLICATE_SESSION);
        scenario.Place(P2, Wallet(1), MAIN, 5, 5);
        scenario.RunPass();

        // ...and only then does the evicted slot's cleanup run.
        scenario.Remove(P1);
        scenario.RunPass();

        Assert.That(Entries(scenario.NextBatch(T0 + 2000)!), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }),
            "the feed must carry the new placement and no exit for a wallet that is still online");

        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 4000), Is.Null);
    }

    /// <summary>
    ///     The same rule the other way round: once the newer connection goes too, the wallet is on no
    ///     slot of this server and the exit is due. Otherwise A1 would turn into "a wallet that ever
    ///     had two sessions never leaves".
    /// </summary>
    [Test]
    public void WhenTheLastConnectionOfAWalletIsCleanedUp_TheExitIsPublished()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Place(P2, Wallet(1), MAIN, 5, 5);
        scenario.RunPass();
        scenario.NextBatch(T0 + 2000);

        scenario.Remove(P1);
        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 4000), Is.Null, "the wallet is still placed on the newer slot");

        scenario.Remove(P2);
        scenario.RunPass();

        Assert.That(Entries(scenario.NextBatch(T0 + 6000)!), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} -" }));
    }

    /// <summary>
    ///     A mid-session ban: the real <see cref="BanEnforcer" /> is handed a list the peer's wallet
    ///     has just joined, and the eviction it enqueues has to end in one exit entry.
    /// </summary>
    [Test]
    public void BanEnforcerEviction_EmitsExactlyOneExitEntry()
    {
        PresenceScenario scenario = OpenedScenario();

        var enforcer = new BanEnforcer(
            NullLogger<BanEnforcer>.Instance, new BanList(), scenario.MessagePipe, scenario.IdentityBoard);

        enforcer.Apply([Wallet(1)]);

        Assert.That(DrainDisconnects(scenario.MessagePipe), Is.EqualTo(new[] { P1 }),
            "the ban must enqueue a disconnect for the banned peer");

        scenario.Remove(P1);
        scenario.RunPass();

        AssertSingleExit(scenario, Wallet(1), MAIN);
    }

    /// <summary>
    ///     A <see cref="PeerDefense" /> kick, driven through the real <see cref="FieldValidator" /> —
    ///     a teleport with no realm, which is a malformed message rather than a move. The peer goes
    ///     PENDING_DISCONNECT at once and the transport disconnect that follows has to produce one
    ///     exit entry, not zero (the peer was on the feed) and not two.
    /// </summary>
    [Test]
    public void PeerDefenseKick_EmitsExactlyOneExitEntry()
    {
        PresenceScenario scenario = OpenedScenario();
        scenario.Authenticate(P1);

        bool accepted = scenario.FieldValidator.ValidateTeleport(
            P1, scenario.Peers[P1], new TeleportRequest { Realm = string.Empty });

        Assert.That(accepted, Is.False);
        Assert.That(scenario.Peers[P1].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
        scenario.Transport.Received(1).Disconnect(P1, DisconnectReason.INVALID_TELEPORT_FIELD);

        scenario.Remove(P1);
        scenario.RunPass();

        AssertSingleExit(scenario, Wallet(1), MAIN);
    }

    /// <summary>
    ///     A realm change is not an exit followed by an entry: the new realm's non-null entry is the
    ///     whole of it, and the old realm is implied because a wallet is in one realm at a time. An
    ///     exit entry here would make a consumer drop the peer it has just been told about.
    /// </summary>
    [Test]
    public void RealmChange_EmitsOneNonNullEntryForTheNewRealmOnly()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Move(P1, COZYFARM, 1, 2);
        scenario.RunPass();

        ParcelChangesBatch batch = scenario.NextBatch(T0 + 2000)!;

        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {COZYFARM} 1,2" }));
    }

    // ── C1.3 coalescing ────────────────────────────────────────────

    /// <summary>
    ///     Within one batch a wallet appears once, carrying its latest state — a peer running across
    ///     parcels costs one entry per interval, not one per step — while a second wallet in the same
    ///     window is untouched by that.
    /// </summary>
    [Test]
    public void ManyMovesInOneInterval_CoalesceToTheLatestStatePerWallet()
    {
        PresenceScenario scenario = OpenedScenario();

        for (var x = 1; x <= 4; x++)
        {
            scenario.Move(P1, MAIN, x, 0);
            scenario.RunPass();
        }

        scenario.Place(P2, Wallet(2), MAIN, 9, 9);
        scenario.RunPass();

        ParcelChangesBatch batch = scenario.NextBatch(T0 + 2000)!;

        Assert.That(Entries(batch), Is.EqualTo(new[]
        {
            $"{Wallet(1)} {MAIN} 4,0",
            $"{Wallet(2)} {MAIN} 9,9",
        }));
    }

    /// <summary>
    ///     An exit that lands on top of an undelivered move supersedes it: the peer has left, so
    ///     publishing where it last stood would leave every consumer holding it there until the next
    ///     snapshot.
    /// </summary>
    [Test]
    public void ExitAfterAnUndeliveredMove_ReplacesIt()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Move(P1, MAIN, 7, 7);
        scenario.RunPass();
        scenario.Remove(P1);
        scenario.RunPass();

        AssertSingleExit(scenario, Wallet(1), MAIN);
    }

    // ── C1.3 in a snapshot ─────────────────────────────────────────

    /// <summary>
    ///     C1.3 is a property of a <em>batch</em>, so it binds a snapshot exactly as it binds a delta
    ///     — and the snapshot is the harder half, because it is assembled by walking slots rather than
    ///     the per-address outbox. A1's window is where that breaks: a duplicate-session kick takes the
    ///     evicted connection off the spatial grid at once, but its tracker slot is cleared only by the
    ///     cleanup a full <c>Peers:DisconnectionCleanTimeoutMs</c> later, so for five seconds by design
    ///     the wallet stands on two slots.
    ///     <para />
    ///     A snapshot that named both would tell a last-write-wins consumer — comms-gatekeeper's
    ///     <c>applyChange</c> is one — that the wallet is wherever the stale entry happened to sort,
    ///     and nothing would correct it until the wallet moved or the first snapshot after the cleanup,
    ///     up to <c>Presence:SnapshotIntervalMs</c> later.
    /// </summary>
    [Test]
    public void AnIntervalSnapshot_OfAWalletOnTwoSlots_CarriesItOnce_AtTheLiveSlot()
    {
        PresenceScenario scenario = DuplicateSlotScenario();

        // The interval deadline passes on a turn with nothing to send, and the next pass answers it.
        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null);

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0 + 62_000);

        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Interval));
        AssertOneEntryPerAddress(batch!);

        Assert.That(Entries(batch!), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }),
            "the snapshot must place the wallet on its live connection, not on the slot being cleaned up");
    }

    /// <summary>
    ///     The same for the snapshot a rebuilt broker connection raises — the one path that makes this
    ///     window <em>more</em> reachable, since a client's reconnect and Pulse's own reconnect are
    ///     often the same network event.
    /// </summary>
    [Test]
    public async Task AReconnectSnapshot_OfAWalletOnTwoSlots_CarriesItOnce_AtTheLiveSlot()
    {
        PresenceScenario scenario = DuplicateSlotScenario();

        // The first open is the one the constructor already raised Start for; every open after it is
        // a reconnect.
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("first"));
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("rebuilt"));

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0 + 4000);

        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Start));
        AssertOneEntryPerAddress(batch!);
        Assert.That(Entries(batch!), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }));
    }

    /// <summary>
    ///     And for the eviction snapshot, which is the worst place to duplicate a wallet: it is the
    ///     batch that exists to repair a delta stream a consumer is known to have holes in, so there
    ///     is nothing else left to correct it with.
    /// </summary>
    [Test]
    public void AnEvictionSnapshot_OfAWalletOnTwoSlots_CarriesItOnce_AtTheLiveSlot()
    {
        PresenceScenario scenario = DuplicateSlotScenario(channelCapacity: 2);

        // The two feeds share Nats:ChannelCapacity and the one CountDropped path, so whatever the
        // cluster feed evicted while this state was built is answered and delivered first — see
        // EvictingScenario. After this the presence outbox is the only thing that can evict.
        scenario.DrainClusterOutbox();
        scenario.RunPass();
        scenario.NextTurn(T0 + 4000);

        // Past the eviction coalescing window, so the eviction below is the one being measured.
        scenario.Clock.UnixTimeMs = T0 + 20_000;

        // Three distinct wallets change inside one interval against a two-entry outbox, so one of
        // them is dropped and the snapshot that repairs it is taken while the first is on two slots.
        scenario.Place(P3, Wallet(3), MAIN, 9, 9);
        scenario.Place(P4, Wallet(4), MAIN, 8, 8);
        scenario.Move(P2, MAIN, 6, 6);
        scenario.RunPass();
        scenario.DrainClusterOutbox();

        List<(ParcelChangesBatch Batch, PresenceSnapshotReason? Reason)> turn =
            scenario.NextTurnWithReasons(T0 + 20_000);

        (ParcelChangesBatch Batch, PresenceSnapshotReason? Reason) published =
            turn.Single(static candidate => candidate.Reason == PresenceSnapshotReason.Eviction);

        AssertOneEntryPerAddress(published.Batch);

        Assert.That(Entries(published.Batch), Is.EqualTo(new[]
        {
            $"{Wallet(1)} {MAIN} 6,6",
            $"{Wallet(3)} {MAIN} 9,9",
            $"{Wallet(4)} {MAIN} 8,8",
        }));
    }

    /// <summary>
    ///     The instant before all of those: between <c>HandshakeHandlerBase.EvictDuplicateSession</c>
    ///     calling <c>transport.Disconnect</c> and the lifecycle event it produces being drained, both
    ///     connections really are in the spatial grid, so one pass observes the wallet twice. The entry
    ///     then has to be the placement that has just handshaked — the slot that was kicked is on its
    ///     way out and its parcel is not news.
    /// </summary>
    [Test]
    public void ASnapshotTakenBeforeTheKickedSlotLeavesTheGrid_CarriesTheNewerPlacementOnce()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Transport.Disconnect(P1, DisconnectReason.DUPLICATE_SESSION);
        scenario.Place(P2, Wallet(1), MAIN, 5, 5);

        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null);

        scenario.RunPass();

        ParcelChangesBatch batch = scenario.NextBatchWithReason(T0 + 62_000).Batch!;

        AssertOneEntryPerAddress(batch);

        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }),
            "the newer placement is this wallet's presence; the kicked slot's parcel is not");
    }

    /// <summary>
    ///     The adverse half of that instant: the kicked client is still walking. Its
    ///     <c>transport.Disconnect</c> leaves as ENet's <em>queued</em> disconnect, so the lifecycle
    ///     event that takes it off the spatial grid waits on the client's acknowledgement — a round
    ///     trip during which its position messages keep being processed and keep moving it between
    ///     cells. The tie pass can therefore observe the <em>stale</em> slot as a change too, and
    ///     which of the two changed last is then decided by <c>grid.GetOccupiedCells()</c> order,
    ///     which is spatial and uncorrelated with which session is newer.
    ///     <para />
    ///     So the tie-break has to be recency of the <em>session</em> — which slot's occupancy started
    ///     later — rather than recency of the placement: the kicked session's occupancy always started
    ///     before the session that kicked it, whereas its last step can easily be the most recent
    ///     thing that happened. On the other reading the snapshot names the parcel the player has just
    ///     left — or, with the two clients in two realms, the realm the player is not in — and strands
    ///     it there for up to <c>Presence:SnapshotIntervalMs</c>: once the kicked slot leaves the grid
    ///     the surviving slot is observed unchanged, so nothing re-states it, and A1 suppresses the
    ///     stale slot's exit.
    /// </summary>
    [Test]
    public void ASnapshotTakenWhileTheKickedSlotIsStillWalking_CarriesTheNewerSessionsSlot()
    {
        // Both intra-cell orders, because the order the pass observes the two peers in is the order
        // they entered the cell they share — and either one is a real arrival order for the two
        // position messages behind it. One of the two always stamps the kicked slot last.
        AssertTheWalkingKickedSlotIsSuperseded(kickedStepsLast: true);
        AssertTheWalkingKickedSlotIsSuperseded(kickedStepsLast: false);
    }

    /// <summary>
    ///     The order of a batch is a function of its content — "two servers with the same state
    ///     produce the same bytes", which is what lets the wire fixtures be compared byte for byte —
    ///     and a duplicated address quietly breaks it: two entries for one wallet compare equal under
    ///     the address-only comparator, so their relative order is whatever the sort behind it makes of
    ///     the slot order rather than something the content decides.
    ///     <para />
    ///     Driven with the transport slots both ways round, which is what a FIFO free list produces:
    ///     the wallet's stale connection is the low index on one server and the high index on the
    ///     other, and the two servers hold the same state either way.
    /// </summary>
    [Test]
    public void TwoServersHoldingOneWalletOnTwoSlots_ProduceTheSameSnapshotBytes()
    {
        PresenceScenario ascending = DuplicateSlotScenario(staleSlot: P1, liveSlot: P2);
        PresenceScenario descending = DuplicateSlotScenario(staleSlot: P2, liveSlot: P1);

        byte[] first = IntervalSnapshotBytes(ascending, T0 + 60_000);

        Assert.That(IntervalSnapshotBytes(ascending, T0 + 130_000), Is.EqualTo(first),
            "two collections of one unchanged state have to produce the same bytes");

        Assert.That(IntervalSnapshotBytes(descending, T0 + 60_000), Is.EqualTo(first),
            "and so do two servers holding that state on different slots");
    }

    /// <summary>
    ///     One wallet on two slots inside A1's window with <b>both</b> slots changing in the tie pass:
    ///     the kicked connection takes one more step, into the grid cell the new session was placed in
    ///     — parcels <c>1,1</c> and <c>5,5</c> both sit inside one 100-unit cell — so the pass observes
    ///     the two in the order they entered that cell, and <paramref name="kickedStepsLast" /> picks
    ///     which order that is.
    /// </summary>
    private static void AssertTheWalkingKickedSlotIsSuperseded(bool kickedStepsLast)
    {
        PresenceScenario scenario = OpenedScenario();

        // The kick with the client still on the wire: no lifecycle event is dispatched, so P1 keeps
        // its place in the spatial grid and its position messages keep being processed — the state
        // PeersManager.HandleDisconnected has not yet undone.
        scenario.Transport.Disconnect(P1, DisconnectReason.DUPLICATE_SESSION);

        if (kickedStepsLast)
        {
            scenario.Place(P2, Wallet(1), MAIN, 5, 5);
            scenario.Move(P1, MAIN, 1, 1);
        }
        else
        {
            scenario.Move(P1, MAIN, 1, 1);
            scenario.Place(P2, Wallet(1), MAIN, 5, 5);
        }

        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null,
            "the interval deadline passes on a turn with nothing to send");

        scenario.RunPass();

        ParcelChangesBatch batch = scenario.NextBatchWithReason(T0 + 62_000).Batch!;

        AssertOneEntryPerAddress(batch);

        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }),
            "this wallet's presence is the session that has just handshaked, not the kicked "
          + $"connection's last step (kicked stepped last: {kickedStepsLast})");
    }

    /// <summary>
    ///     A wallet on two slots, in the state A1 leaves behind: the evicted connection is off the
    ///     spatial grid (phase 1 of the disconnect, which the lifecycle event does at once) but its
    ///     tracker slot is still there, because only <c>CleanupDisconnectedPeer</c> clears it and that
    ///     runs a full <c>Peers:DisconnectionCleanTimeoutMs</c> later. The opening snapshot and the new
    ///     placement's delta have both been delivered, so the next batch is the snapshot under test.
    /// </summary>
    private static PresenceScenario DuplicateSlotScenario(
        int channelCapacity = 1024, PeerIndex? staleSlot = null, PeerIndex? liveSlot = null)
    {
        PeerIndex stale = staleSlot ?? P1;
        PeerIndex live = liveSlot ?? P2;

        var scenario = new PresenceScenario(channelCapacity: channelCapacity);

        scenario.Place(stale, Wallet(1), MAIN, -1, 0);
        scenario.Authenticate(stale);
        scenario.RunPass();
        scenario.NextBatch(T0);

        // The second client instance handshakes: the existing session is kicked, its lifecycle event
        // takes it off the grid, and the new session is allocated a free index and placed at once.
        scenario.Transport.Disconnect(stale, DisconnectReason.DUPLICATE_SESSION);
        scenario.DispatchDisconnected(stale);
        scenario.Place(live, Wallet(1), MAIN, 5, 5);
        scenario.RunPass();

        Assert.That(Entries(scenario.NextBatch(T0 + 2000)!), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }),
            "the new placement goes out as a delta, and no exit does (A1)");

        return scenario;
    }

    /// <summary>
    ///     One interval snapshot's bytes, with the two fields that are meant to differ between batches
    ///     — <c>seq</c> and <c>server_time</c> — zeroed, so what is compared is the entries and the
    ///     order they were written in.
    /// </summary>
    private static byte[] IntervalSnapshotBytes(PresenceScenario scenario, long at)
    {
        Assert.That(scenario.NextBatch(at), Is.Null, "the deadline passes on a turn with nothing to send");

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(at + 2000);

        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Interval));

        ParcelChangesBatch normalized = batch!.Clone();

        normalized.Seq = 0;
        normalized.ServerTime = 0;

        return PresenceScenario.Serialize(normalized);
    }

    /// <summary>
    ///     C1.3 read off a whole batch: each address once, and ascending — which is also what makes
    ///     the batch order a function of its content, since the comparator ties on equal addresses and
    ///     the sort behind it is not stable.
    /// </summary>
    private static void AssertOneEntryPerAddress(ParcelChangesBatch batch)
    {
        string[] addresses = batch.Changes.Select(static change => change.Address).ToArray();

        Assert.That(addresses, Is.Unique, "within one batch a wallet appears at most once (C1.3)");
        Assert.That(addresses, Is.Ordered.Using<string>(StringComparer.Ordinal));
    }

    // ── C1.4 cadence ───────────────────────────────────────────────

    /// <summary>
    ///     The first batch of the process is a full snapshot: consumers hold nothing for this
    ///     <c>server_name</c> yet, so a delta would be a diff against nothing.
    /// </summary>
    [Test]
    public void FirstBatch_IsASnapshot_LabelledStart()
    {
        var scenario = new PresenceScenario();

        scenario.Place(P1, Wallet(1), MAIN, -1, 0);
        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0);

        Assert.That(batch!.Snapshot, Is.True);
        Assert.That(batch.Seq, Is.EqualTo(1u), "seq starts at 1 and is shared by snapshots and deltas");
        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Start));
    }

    /// <summary>
    ///     <c>Presence:SnapshotIntervalMs</c> is a recovery deadline: whatever a consumer missed, a
    ///     full snapshot corrects it within that window. Measured from the snapshot published, so the
    ///     opening one resets it.
    /// </summary>
    [Test]
    public void AfterTheSnapshotInterval_TheNextBatchIsASnapshot_LabelledInterval()
    {
        PresenceScenario scenario = OpenedScenario();

        // Nothing moved, so this turn sends nothing — but it is the turn on which the deadline
        // passes, so it raises the request the next pass answers.
        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null);

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0 + 62_000);

        Assert.That(batch!.Snapshot, Is.True);
        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Interval));
        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} -1,0" }),
            "a snapshot carries one non-null entry per active peer");
    }

    /// <summary>
    ///     One interval, one snapshot. The request the deadline raises is answered by the next pass
    ///     and published by the turn after that, so for two turns the deadline is still nominally
    ///     past — and a second request raised in that window costs a whole extra snapshot of this
    ///     server's population, every minute, saying exactly what the first one said.
    /// </summary>
    [Test]
    public void TheIntervalSnapshot_IsPublishedOnce_NotAgainWhileItIsInFlight()
    {
        PresenceScenario scenario = OpenedScenario();

        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null, "the deadline passes on a turn with nothing to send");

        scenario.RunPass();

        Assert.That(scenario.NextBatchWithReason(T0 + 62_000).Reason, Is.EqualTo(PresenceSnapshotReason.Interval));

        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 64_000), Is.Null,
            "the snapshot has just gone out — the deadline is met, not still pending");

        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 66_000), Is.Null);
    }

    /// <summary>
    ///     The outbox's one path to real loss is eviction, and a consumer applying the deltas either
    ///     side of one would hold a peer at a parcel it has left. So an eviction forces a snapshot:
    ///     consumers never run on a delta stream that is known to be incomplete.
    /// </summary>
    [Test]
    public void OutboxEviction_ForcesASnapshot_LabelledEviction_OnTheVeryNextTurn()
    {
        PresenceScenario scenario = EvictingScenario();

        // Past the eviction coalescing window, so the eviction below is the one being measured
        // rather than one already covered by an earlier snapshot.
        scenario.Clock.UnixTimeMs = T0 + 20_000;

        // Three wallets change inside one interval against a two-entry outbox, so the third insert
        // drops one of them — a change that no consumer will ever receive.
        scenario.Move(P1, MAIN, -2, 0);
        scenario.Move(P2, MAIN, 6, 5);
        scenario.Move(P3, COZYFARM, 1, 1);
        scenario.RunPass();

        // No second pass: the eviction was caused by the deltas this pass published, and the pass
        // answers the request it raised before it returns. "Immediately after any outbox eviction"
        // is the next turn, not the turn after the pass after it.
        List<(ParcelChangesBatch Batch, PresenceSnapshotReason? Reason)> turn =
            scenario.NextTurnWithReasons(T0 + 20_000);

        Assert.That(turn, Has.Count.EqualTo(2), "the pending deltas, and then the snapshot behind them");

        Assert.That(turn[0].Reason, Is.Null);
        Assert.That(turn[0].Batch.Snapshot, Is.False);

        // Which of the three was dropped is the outbox's admission order, not a contract; that two
        // survived and still go out is the A2 half — a snapshot does not swallow them.
        Assert.That(turn[0].Batch.Changes, Has.Count.EqualTo(2),
            "what survived the eviction still goes out — the snapshot repairs what did not");

        Assert.That(turn[1].Reason, Is.EqualTo(PresenceSnapshotReason.Eviction));
        Assert.That(turn[1].Batch.Snapshot, Is.True);
        Assert.That(turn[1].Batch.Seq, Is.EqualTo(turn[0].Batch.Seq + 1), "the snapshot takes the next seq");

        Assert.That(Entries(turn[1].Batch), Is.EqualTo(new[]
        {
            $"{Wallet(1)} {MAIN} -2,0",
            $"{Wallet(2)} {MAIN} 6,5",
            $"{Wallet(3)} {COZYFARM} 1,1",
        }), "the snapshot must carry the evicted peer's current state, which is what repairs the loss");
    }

    /// <summary>
    ///     An eviction means the broker is already behind, and a full-population snapshot is the
    ///     largest message this feed can produce — so raising one per batch for as long as the outbox
    ///     keeps evicting is the worst thing to do at that moment. Eviction snapshots are coalesced to
    ///     at most one per
    ///     <c>Presence:SnapshotIntervalMs / NatsPublisher.EVICTION_SNAPSHOT_INTERVAL_DIVISOR</c> — 15 s
    ///     on the defaults — while the first one is still immediate, which is the half that repairs
    ///     the loss.
    /// </summary>
    [Test]
    public void SustainedEviction_RaisesOneSnapshotPerCoalescingWindow_NotOnePerBatch()
    {
        PresenceScenario scenario = EvictingScenario();

        Assert.That(EvictOnce(scenario, step: 1, at: T0 + 20_000), Is.True, "the first eviction snapshots at once");

        // Same window: the loss is repaired by the snapshot that has just gone out, and a second full
        // snapshot two seconds later would say the same thing at the worst possible moment.
        Assert.That(EvictOnce(scenario, step: 2, at: T0 + 22_000), Is.False);
        Assert.That(EvictOnce(scenario, step: 3, at: T0 + 24_000), Is.False);

        // Past the window, a still-evicting outbox is worth restating the world for.
        Assert.That(EvictOnce(scenario, step: 4, at: T0 + 20_000 + 15_000), Is.True);
    }

    /// <summary>
    ///     Moves three wallets against a two-entry outbox at wall-clock <paramref name="at" /> — so
    ///     one change is evicted — and reports whether the turn that follows carried an eviction
    ///     snapshot. The clock is set before the pass, because the request is raised inside it.
    /// </summary>
    private static bool EvictOnce(PresenceScenario scenario, int step, long at)
    {
        scenario.Clock.UnixTimeMs = at;

        scenario.Move(P1, MAIN, -2 - step, 0);
        scenario.Move(P2, MAIN, 6 + step, 5);
        scenario.Move(P3, COZYFARM, 1 + step, 1);
        scenario.RunPass();
        scenario.DrainClusterOutbox();

        return scenario.NextTurnWithReasons(at)
                       .Any(static published => published.Reason == PresenceSnapshotReason.Eviction);
    }

    /// <summary>
    ///     A snapshot never discards a pending change (A2). It supersedes the <em>placements</em>
    ///     among them — it states each of those peers' position itself — but it cannot name an exit:
    ///     the departed peer's slot is already cleared, so <c>CollectLivePresence</c> does not list
    ///     it. Clearing the pending set therefore made that exit path publish nothing at all, while
    ///     C1 §2 says it publishes exactly one entry.
    ///     <para />
    ///     So the batch that was pending when the snapshot was collected goes out first, under its own
    ///     <c>seq</c>, and the snapshot follows on the next one — both on the wire, in that order.
    /// </summary>
    [Test]
    public void ASnapshotDoesNotDiscardAPendingExit_ItPublishesItFirst()
    {
        PresenceScenario scenario = OpenedScenario();

        // The interval deadline passes on a turn with nothing to send, raising the request.
        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null);

        // The peer leaves before the pass that answers it, so the snapshot cannot name it.
        scenario.Remove(P1);
        scenario.RunPass();

        List<(ParcelChangesBatch Batch, PresenceSnapshotReason? Reason)> turn =
            scenario.NextTurnWithReasons(T0 + 62_000);

        Assert.That(turn, Has.Count.EqualTo(2), "the pending exit, and then the snapshot");

        Assert.That(turn[0].Batch.Snapshot, Is.False);
        Assert.That(Entries(turn[0].Batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} -" }),
            "the exit is the pending batch, and it has to reach consumers");

        Assert.That(turn[1].Batch.Snapshot, Is.True);
        Assert.That(turn[1].Reason, Is.EqualTo(PresenceSnapshotReason.Interval));
        Assert.That(turn[1].Batch.Seq, Is.EqualTo(turn[0].Batch.Seq + 1));
        Assert.That(Entries(turn[1].Batch), Is.Empty, "and the server is empty, which the snapshot says");
    }

    /// <summary>
    ///     A rebuilt broker connection restates the world. A consumer that subscribed while Pulse was
    ///     disconnected holds nothing for this <c>server_name</c>, and the consumer rule tells it to
    ///     hold and wait on anything that is not a snapshot — so resuming mid-delta leaves it frozen
    ///     for up to a snapshot interval.
    /// </summary>
    [Test]
    public async Task AReconnect_MakesTheNextBatchASnapshot()
    {
        PresenceScenario scenario = OpenedScenario();

        // The first open is the one the constructor already raised Start for; every open after it is
        // a reconnect.
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("first"));
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("rebuilt"));

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0 + 2000);

        Assert.That(batch!.Snapshot, Is.True);
        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Start));
        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} -1,0" }));
    }

    /// <summary>
    ///     <c>PeerIndex</c> is a recycled transport slot. Nothing releases one today except the
    ///     cleanup that clears the tracker's slot, but the codebase already guards the same class of
    ///     aliasing for the observer view — and if a slot were ever reused without it, a new wallet
    ///     landing on the departed one's parcel would publish nothing, and the next snapshot would
    ///     report the peer that left as standing there.
    /// </summary>
    [Test]
    public void ANewWalletOnARecycledSlot_IsPublished_EvenAtTheSameParcel()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Place(P1, Wallet(2), MAIN, -1, 0);
        scenario.RunPass();

        Assert.That(Entries(scenario.NextBatch(T0 + 2000)!), Is.EqualTo(new[] { $"{Wallet(2)} {MAIN} -1,0" }));
    }

    // ── C1.5 canonical identifiers ─────────────────────────────────

    /// <summary>
    ///     Realm and address reach the wire lowercase however they arrived, and <c>server_name</c> is
    ///     the same string for the life of the process — consumers key their per-server state on it,
    ///     so a value that drifted would look like a second server.
    /// </summary>
    [Test]
    public void RealmAndAddress_AreLowercased_AndServerNameIsStable()
    {
        var scenario = new PresenceScenario("pulse-1");

        scenario.Register(P1, "0x00000000000000000000000000000000000000AB");
        scenario.Teleport(P1, "CozyFarm.dcl.eth", 5, 6);
        scenario.RunPass();

        ParcelChangesBatch opening = scenario.NextBatch(T0)!;

        Assert.That(Entries(opening),
            Is.EqualTo(new[] { $"0x00000000000000000000000000000000000000ab {COZYFARM} 5,6" }));

        scenario.Move(P1, COZYFARM, 6, 6);
        scenario.RunPass();

        ParcelChangesBatch delta = scenario.NextBatch(T0 + 2000)!;

        Assert.That(delta.ServerName, Is.EqualTo(opening.ServerName).And.EqualTo("pulse-1"));
    }

    // ── Rollback default ──────────────────────────────────────────

    /// <summary>
    ///     The feed follows the existing NATS gating, so a deploy that changes no configuration
    ///     publishes nothing — the tracker is inert and every entry point a no-op. This is the
    ///     property that makes the whole feature safe to merge before it is switched on anywhere.
    /// </summary>
    [Test]
    public void WithNoBrokerConfigured_TheTrackerPublishesNothing()
    {
        IClusterFeedPublisher feed = Substitute.For<IClusterFeedPublisher>();
        ParcelChangeTracker tracker = TrackerWith(feed, natsUrl: string.Empty, presenceEnabled: true);

        Assert.That(tracker.Enabled, Is.False);

        tracker.ObservePass(OnePeerPass());
        tracker.OnPeerRemoved(P1);

        Assert.That(feed.ReceivedCalls(), Is.Empty);
    }

    /// <summary>
    ///     And the switch for the feed alone: a broker is configured, but <c>Presence:Enabled</c> is
    ///     off, so clustering and <c>engine.islands</c> carry on and only presence goes quiet.
    /// </summary>
    [Test]
    public void WithPresenceDisabled_TheTrackerPublishesNothing()
    {
        IClusterFeedPublisher feed = Substitute.For<IClusterFeedPublisher>();
        ParcelChangeTracker tracker = TrackerWith(feed, natsUrl: "nats://localhost:4222", presenceEnabled: false);

        Assert.That(tracker.Enabled, Is.False);

        tracker.ObservePass(OnePeerPass());
        tracker.OnPeerRemoved(P1);

        Assert.That(feed.ReceivedCalls(), Is.Empty);
    }

    // ── Helpers ───────────────────────────────────────────────────

    /// <summary>
    ///     A scenario whose opening snapshot has already gone out, holding one peer in main — the
    ///     state every delta test starts from, since a pending snapshot would otherwise absorb the
    ///     change under test.
    /// </summary>
    private static PresenceScenario OpenedScenario()
    {
        var scenario = new PresenceScenario();

        scenario.Place(P1, Wallet(1), MAIN, -1, 0);
        scenario.Authenticate(P1);
        scenario.RunPass();
        scenario.NextBatch(T0);

        return scenario;
    }

    /// <summary>
    ///     Each entry as <c>"address realm x,y"</c>, or <c>"address realm -"</c> for an exit, so a
    ///     failure prints what the batch said instead of a protobuf dump.
    /// </summary>
    private static string[] Entries(ParcelChangesBatch batch) =>
        batch.Changes
             .Select(static change => change.Parcel is { } parcel
                  ? $"{change.Address} {change.Realm} {parcel.X},{parcel.Y}"
                  : $"{change.Address} {change.Realm} -")
             .ToArray();

    private static void AssertSingleExit(PresenceScenario scenario, string address, string realm)
    {
        Assert.That(Entries(scenario.NextBatch(T0 + 2000)!), Is.EqualTo(new[] { $"{address} {realm} -" }));

        scenario.RunPass();

        Assert.That(scenario.NextBatch(T0 + 4000), Is.Null, "the exit must be published exactly once");
    }

    /// <summary>
    ///     Three peers in a two-entry outbox, with the opening snapshot delivered and the cluster feed
    ///     drained — so the next pass in which all three move is the one that evicts, and nothing
    ///     else has evicted before it.
    ///     <para />
    ///     That last part needs doing explicitly: the two feeds share <c>Nats:ChannelCapacity</c> and
    ///     the one <c>CountDropped</c> path, and the cluster feed publishes three first-time
    ///     assignments in the opening pass, so it evicts one of its own and raises the presence
    ///     snapshot request that goes with it. Real, and the reason eviction snapshots are coalesced
    ///     at all — but not what these two tests are about, so it is answered and cleared here.
    /// </summary>
    private static PresenceScenario EvictingScenario()
    {
        var scenario = new PresenceScenario(channelCapacity: 2);

        scenario.Place(P1, Wallet(1), MAIN, -1, 0);
        scenario.Place(P2, Wallet(2), MAIN, 5, 5);
        scenario.Place(P3, Wallet(3), COZYFARM, 0, 0);
        scenario.RunPass();
        scenario.NextBatch(T0);

        Assert.That(scenario.Publisher.DroppedCount, Is.EqualTo(1),
            "the shared outbox bound evicts one of the three first-time cluster assignments");

        Assert.That(scenario.DrainClusterOutbox(), Is.GreaterThan(0));

        // Answers and delivers the snapshot that cluster-feed eviction asked for.
        scenario.RunPass();
        scenario.NextBatch(T0 + 2000);

        return scenario;
    }

    private static PeerIndex[] DrainDisconnects(MessagePipe pipe)
    {
        var disconnected = new List<PeerIndex>();

        while (pipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage message))
            if (message.IsDisconnect)
                disconnected.Add(message.To);

        return disconnected.ToArray();
    }

    private static ParcelChangeTracker TrackerWith(IClusterFeedPublisher feed, string natsUrl, bool presenceEnabled) =>
        new (
            feed,
            PresenceTestFactory.Encoder(),
            Options.Create(new PresenceOptions { Enabled = presenceEnabled }),
            Options.Create(new NatsOptions { Url = natsUrl }),
            PresenceScenario.MAX_PEERS);

    private static ClusterPass OnePeerPass() =>
        new (
            [],
            [new ClusterPeerInfo(P1, Wallet(1), "C1", MAIN, System.Numerics.Vector3.Zero, 0)],
            new string?[PresenceScenario.MAX_PEERS]);
}
