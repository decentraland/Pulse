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
///     The guarantees Pulse holds for <c>engine.parcel_changes</c> (iteration-2 C1), one test per
///     clause, all driven through the real pass, outbox and peer-lifecycle cleanup. The bytes they
///     produce are pinned in <see cref="PresenceWireFixtureTests" />.
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

    /// <summary>The baseline exit: a client close or an ENet timeout, carrying the realm last seen.</summary>
    [Test]
    public void CleanDisconnect_EmitsExactlyOneExitEntry()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Remove(P1);
        scenario.RunPass();

        AssertSingleExit(scenario, Wallet(1), MAIN);
    }

    /// <summary>A peer that never authenticated was never placed, so it never reached the feed.</summary>
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

    /// <summary>The feed's half of a duplicate-session kick — <c>HandshakeHandlerTests</c> has the other.</summary>
    [Test]
    public void DuplicateSessionKick_EmitsExactlyOneExitEntry()
    {
        PresenceScenario scenario = OpenedScenario();

        scenario.Transport.Disconnect(P1, DisconnectReason.DUPLICATE_SESSION);
        scenario.Remove(P1);
        scenario.RunPass();

        AssertSingleExit(scenario, Wallet(1), MAIN);
    }

    /// <summary>Why the exit is wallet-scoped (A1): the evicted slot is cleaned last.</summary>
    [Test]
    public void DuplicateSessionKick_ForAWalletAlreadyPlacedOnANewerSlot_EmitsNoExit()
    {
        PresenceScenario scenario = OpenedScenario();

        // The second client handshakes: the old session is kicked, the new one placed on its own slot.
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

    /// <summary>A1 the other way round: on no slot of this server, the wallet's exit is finally due.</summary>
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

    /// <summary>A mid-session ban, driven through the real <see cref="BanEnforcer" />.</summary>
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

    /// <summary>A <see cref="PeerDefense" /> kick, driven through the real <see cref="FieldValidator" />.</summary>
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

    /// <summary>A realm change is one non-null entry for the new realm, not an exit and an entry.</summary>
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

    /// <summary>The second wallet is there to show the coalescing is per wallet, not per batch.</summary>
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

    /// <summary>An exit supersedes an undelivered move — the peer has left, so its parcel is not news.</summary>
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
    ///     C1.3 binds a snapshot too, and the snapshot is the harder half: it walks slots, and inside
    ///     A1's window — a whole <c>Peers:DisconnectionCleanTimeoutMs</c> — a wallet is on two of them.
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

    /// <summary>The same for a rebuilt broker connection, the likeliest way into that window.</summary>
    [Test]
    public async Task AReconnectSnapshot_OfAWalletOnTwoSlots_CarriesItOnce_AtTheLiveSlot()
    {
        PresenceScenario scenario = DuplicateSlotScenario();

        // The constructor already raised Start for the first open; every open after it is a reconnect.
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("first"));
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("rebuilt"));

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0 + 4000);

        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Start));
        AssertOneEntryPerAddress(batch!);
        Assert.That(Entries(batch!), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }));
    }

    /// <summary>And for the eviction snapshot, which exists to repair what nothing else will correct.</summary>
    [Test]
    public void AnEvictionSnapshot_OfAWalletOnTwoSlots_CarriesItOnce_AtTheLiveSlot()
    {
        PresenceScenario scenario = DuplicateSlotScenario(channelCapacity: 2);

        // Both feeds share Nats:ChannelCapacity, so clear the cluster feed's own eviction first.
        scenario.DrainClusterOutbox();
        scenario.RunPass();
        scenario.NextTurn(T0 + 4000);

        // Past the eviction coalescing window, so the eviction below is the one being measured.
        scenario.Clock.UnixTimeMs = T0 + 20_000;

        // Three wallets change against a two-entry outbox, so one is dropped and a snapshot repairs it.
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
    ///     The instant before all of those: between the kick's <c>transport.Disconnect</c> and the
    ///     lifecycle event it produces, both connections are in the grid and one pass observes both.
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
    ///     The adverse half of that instant: the kicked client is still walking, because ENet's queued
    ///     disconnect waits on its ack. So the tie-break is recency of the <em>session</em>, not of the
    ///     placement — the kicked session always started first, but its last step can be the newest thing.
    /// </summary>
    [Test]
    public void ASnapshotTakenWhileTheKickedSlotIsStillWalking_CarriesTheNewerSessionsSlot()
    {
        // Both traversal orders: one of them always stamps the kicked slot last.
        AssertTheWalkingKickedSlotIsSuperseded(kickedObservedLast: true);
        AssertTheWalkingKickedSlotIsSuperseded(kickedObservedLast: false);
    }

    /// <summary>
    ///     Batch order is a function of content — what lets the wire fixtures compare byte for byte —
    ///     but two entries for one wallet tie under the address-only comparator, whose sort is not stable.
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
    ///     One wallet on two slots, both observed in one pass, the kicked connection a step ahead —
    ///     built by hand, since <c>ClusterTracker.RunPass</c> now collects only the live binding.
    /// </summary>
    private static void AssertTheWalkingKickedSlotIsSuperseded(bool kickedObservedLast)
    {
        PresenceScenario scenario = OpenedScenario();

        // The kick with the client still on the wire: no lifecycle event yet, so P1 keeps its cell.
        scenario.Transport.Disconnect(P1, DisconnectReason.DUPLICATE_SESSION);

        // The kicked client's last step moves P1 into P2's cell — parcels 1,1 and 5,5 share one cell.
        scenario.Place(P2, Wallet(1), MAIN, 5, 5);
        scenario.Move(P1, MAIN, 1, 1);

        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null,
            "the interval deadline passes on a turn with nothing to send");

        // P2's occupancy starts in this pass; P1's started in the opening one.
        ClusterPeerInfo kicked = TiePassMember(scenario, P1, 1, 1);
        ClusterPeerInfo live = TiePassMember(scenario, P2, 5, 5);

        scenario.ParcelChanges.ObservePass(
            kickedObservedLast ? TiePass(live, kicked) : TiePass(kicked, live));

        ParcelChangesBatch batch = scenario.NextBatchWithReason(T0 + 62_000).Batch!;

        AssertOneEntryPerAddress(batch);

        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }),
            "this wallet's presence is the session that has just handshaked, not the kicked "
          + $"connection's last step (kicked observed last: {kickedObservedLast})");
    }

    /// <summary>One member of a hand-built pass, carrying the wallet both sessions authenticate as.</summary>
    private static ClusterPeerInfo TiePassMember(
        PresenceScenario scenario, PeerIndex peer, int parcelX, int parcelY) =>
        new (peer, Wallet(1), "C1", MAIN, PresenceScenario.CentreOf(parcelX, parcelY),
            scenario.ParcelEncoder.Encode(parcelX, parcelY));

    /// <summary>A pass carrying the given members in the order the tracker observes them in.</summary>
    private static ClusterPass TiePass(params ClusterPeerInfo[] members)
    {
        var clusterIdByPeer = new string?[PresenceScenario.MAX_PEERS];

        foreach (ClusterPeerInfo member in members)
            clusterIdByPeer[(int)member.Peer.Value] = member.ClusterId;

        return new ClusterPass(
            [new ClusterInfo("C1", MAIN, members.Length, PresenceScenario.CentreOf(5, 5), 0f)],
            members,
            clusterIdByPeer);
    }

    /// <summary>
    ///     A wallet on two slots, the state A1 leaves behind: the evicted connection is off the grid but
    ///     its tracker slot survives until <c>CleanupDisconnectedPeer</c>. Opening batches delivered.
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

        // The second client handshakes: the existing session is kicked and taken off the grid by its
        // lifecycle event, and the new one is placed on a free index at once.
        scenario.Transport.Disconnect(stale, DisconnectReason.DUPLICATE_SESSION);
        scenario.DispatchDisconnected(stale);
        scenario.Place(live, Wallet(1), MAIN, 5, 5);
        scenario.RunPass();

        Assert.That(Entries(scenario.NextBatch(T0 + 2000)!), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} 5,5" }),
            "the new placement goes out as a delta, and no exit does (A1)");

        return scenario;
    }

    /// <summary>
    ///     One interval snapshot's bytes with <c>seq</c> and <c>server_time</c> zeroed, so what is
    ///     compared is the entries and the order they were written in.
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

    /// <summary>C1.3 read off a whole batch: each address once, and ascending.</summary>
    private static void AssertOneEntryPerAddress(ParcelChangesBatch batch)
    {
        string[] addresses = batch.Changes.Select(static change => change.Address).ToArray();

        Assert.That(addresses, Is.Unique, "within one batch a wallet appears at most once (C1.3)");
        Assert.That(addresses, Is.Ordered.Using<string>(StringComparer.Ordinal));
    }

    // ── C1.4 cadence ───────────────────────────────────────────────

    /// <summary>The process's first batch is a snapshot — a delta would be a diff against nothing.</summary>
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

    /// <summary>The deadline is measured from the snapshot published, so the opening one resets it.</summary>
    [Test]
    public void AfterTheSnapshotInterval_TheNextBatchIsASnapshot_LabelledInterval()
    {
        PresenceScenario scenario = OpenedScenario();

        // Nothing moved, so this turn sends nothing — but the deadline passes on it, raising the request.
        Assert.That(scenario.NextBatch(T0 + 60_000), Is.Null);

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0 + 62_000);

        Assert.That(batch!.Snapshot, Is.True);
        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Interval));
        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} -1,0" }),
            "a snapshot carries one non-null entry per active peer");
    }

    /// <summary>One interval, one snapshot: the deadline stays past while the request is in flight.</summary>
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

    /// <summary>Eviction is the outbox's one path to real loss, so it forces a snapshot at once.</summary>
    [Test]
    public void OutboxEviction_ForcesASnapshot_LabelledEviction_OnTheVeryNextTurn()
    {
        PresenceScenario scenario = EvictingScenario();

        // Past the eviction coalescing window, so the eviction below is the one being measured.
        scenario.Clock.UnixTimeMs = T0 + 20_000;

        // Three wallets change against a two-entry outbox, so the third insert drops one of them.
        scenario.Move(P1, MAIN, -2, 0);
        scenario.Move(P2, MAIN, 6, 5);
        scenario.Move(P3, COZYFARM, 1, 1);
        scenario.RunPass();

        // No second pass: the pass answers the request its own deltas raised before it returns, so
        // "immediately after an eviction" is this turn.
        List<(ParcelChangesBatch Batch, PresenceSnapshotReason? Reason)> turn =
            scenario.NextTurnWithReasons(T0 + 20_000);

        Assert.That(turn, Has.Count.EqualTo(2), "the pending deltas, and then the snapshot behind them");

        Assert.That(turn[0].Reason, Is.Null);
        Assert.That(turn[0].Batch.Snapshot, Is.False);

        // Which one was dropped is admission order, not contract; that the other two go out is A2.
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
    ///     An eviction means the broker is already behind and a full snapshot is this feed's largest
    ///     message, so after the first they are coalesced to one per
    ///     <c>Presence:SnapshotIntervalMs / NatsPublisher.EVICTION_SNAPSHOT_INTERVAL_DIVISOR</c> — 15 s.
    /// </summary>
    [Test]
    public void SustainedEviction_RaisesOneSnapshotPerCoalescingWindow_NotOnePerBatch()
    {
        PresenceScenario scenario = EvictingScenario();

        Assert.That(EvictOnce(scenario, step: 1, at: T0 + 20_000), Is.True, "the first eviction snapshots at once");

        // Same window: the snapshot that has just gone out already repaired the loss.
        Assert.That(EvictOnce(scenario, step: 2, at: T0 + 22_000), Is.False);
        Assert.That(EvictOnce(scenario, step: 3, at: T0 + 24_000), Is.False);

        // Past the window, a still-evicting outbox is worth restating the world for.
        Assert.That(EvictOnce(scenario, step: 4, at: T0 + 20_000 + 15_000), Is.True);
    }

    /// <summary>
    ///     Moves three wallets against a two-entry outbox at <paramref name="at" /> — evicting one — and
    ///     reports whether the next turn carried an eviction snapshot. The clock leads the pass.
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
    ///     A snapshot never discards a pending change (A2): it supersedes the placements among them, but
    ///     it cannot name an exit, so the pending batch goes out first under its own <c>seq</c>.
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

    /// <summary>A rebuilt connection restates the world; a subscriber that arrived mid-outage holds nothing.</summary>
    [Test]
    public async Task AReconnect_MakesTheNextBatchASnapshot()
    {
        PresenceScenario scenario = OpenedScenario();

        // The constructor already raised Start for the first open; every open after it is a reconnect.
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("first"));
        await scenario.Publisher.OnConnectionOpened(null, new NatsEventArgs("rebuilt"));

        scenario.RunPass();

        (ParcelChangesBatch? batch, PresenceSnapshotReason? reason) = scenario.NextBatchWithReason(T0 + 2000);

        Assert.That(batch!.Snapshot, Is.True);
        Assert.That(reason, Is.EqualTo(PresenceSnapshotReason.Start));
        Assert.That(Entries(batch), Is.EqualTo(new[] { $"{Wallet(1)} {MAIN} -1,0" }));
    }

    /// <summary>
    ///     <c>PeerIndex</c> is a recycled transport slot: a new wallet landing on a departed one's parcel
    ///     has to publish, or the next snapshot would report the peer that left as standing there.
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
    ///     Realm and address reach the wire lowercase however they arrived, and <c>server_name</c> is one
    ///     string for the life of the process — a value that drifted would look like a second server.
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

    /// <summary>The existing NATS gating governs the feed: no config change, nothing published.</summary>
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

    /// <summary>And the switch for the feed alone: clustering and <c>engine.islands</c> carry on.</summary>
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
    ///     One peer in main with the opening snapshot already delivered — where every delta test starts,
    ///     since a pending snapshot would otherwise absorb the change under test.
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

    /// <summary>Each entry as <c>"address realm x,y"</c>, or <c>"address realm -"</c> for an exit.</summary>
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
    ///     Three peers in a two-entry outbox with the opening snapshot delivered and the cluster feed
    ///     drained. That draining needs doing explicitly: the cluster feed evicts one of its own three
    ///     first-time assignments in the opening pass, raising a snapshot request that is not under test.
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
