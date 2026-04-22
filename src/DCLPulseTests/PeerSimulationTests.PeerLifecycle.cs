using Decentraland.Pulse;
using NSubstitute;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void ObserverViews_CleanedUpWhenPeerDisconnects()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined — creates an entry in observerViews

        Assert.That(simulation.observerViews, Does.ContainKey(observer));

        // Transition observer to DISCONNECTING
        peers[observer].ConnectionState = PeerConnectionState.DISCONNECTING;
        peers[observer].TransportState = peers[observer].TransportState with { DisconnectionTime = 0 };

        // Advance time past PEER_DISCONNECTION_CLEAN_TIMEOUT (5000ms)
        timeProvider.MonotonicTime.Returns(6000u);

        simulation.SimulateTick(peers, tickCounter: 1);

        // The peer should be removed from peers
        Assert.That(peers, Does.Not.ContainKey(observer));

        // observerViews should also be cleaned up
        Assert.That(simulation.observerViews, Does.Not.ContainKey(observer),
            "observerViews leaked — entry not removed when peer disconnected");
    }

    /// <summary>
    ///     ENet recycles peer.ID across connect/disconnect. If a disconnecting peer's PeerIndex
    ///     is reassigned to a new wallet before the observer's stale view is evicted by
    ///     SweepStaleViews, the observer silently reuses the stale view — no PlayerJoined for the
    ///     new player, deltas diffed against the old peer's baseline, wrong wallet cached on the
    ///     client. The fix must emit PlayerLeft to every observer synchronously on disconnect
    ///     (or key views by (PeerIndex, epoch)) so reuse cannot collide with stale state.
    /// </summary>
    [Test]
    public void PlayerJoined_SentWhenPeerIndexIsRecycledForNewWallet()
    {
        // Peer A (idx=subject) with wallet "0xSUBJECT_WALLET" becomes visible — PlayerJoined fires.
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Peer A disconnects. Register it in peers as DISCONNECTING and advance past the
        // PEER_DISCONNECTION_CLEAN_TIMEOUT (5000ms) so CleanupDisconnectedPeer runs for it.
        peers[subject] = new PeerState(PeerConnectionState.DISCONNECTING)
        {
            TransportState = new PeerTransportState(ConnectionTime: 0, DisconnectionTime: 0),
        };
        SetVisibleSubjects(); // subject not visible while gone
        timeProvider.MonotonicTime.Returns(6000u);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        Assert.That(peers, Does.Not.ContainKey(subject),
            "precondition: cleanup removed peer A from peers");

        // ENet recycles peer A's ID. A new peer B lands on the same PeerIndex with a different
        // wallet. Re-register identity and republish the snapshot — the observer's stale view
        // for this PeerIndex has NOT been swept (tick 2 is well before the next SWEEP_INTERVAL).
        identityBoard.Set(subject, "0xNEW_WALLET");
        PublishSnapshot(subject, seq: 1);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> joinedMessages = DrainAllMessages()
           .Where(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined)
           .ToList();

        Assert.That(joinedMessages, Is.Not.Empty,
            "PlayerJoined was not re-sent after PeerIndex was recycled for a different wallet — "
            + "observer will keep the previous wallet and diff deltas against stale baseline");
        Assert.That(joinedMessages, Has.Count.EqualTo(1));
        Assert.That(joinedMessages[0].Message.PlayerJoined.UserId, Is.EqualTo("0xNEW_WALLET"));
    }

    /// <summary>
    ///     Cleanup must wipe every per-peer board so a future peer landing on the same
    ///     PeerIndex doesn't inherit any prior state. This is the precondition for any
    ///     allocator that recycles slot values — if cleanup misses a board, the recycled
    ///     PeerIndex carries ghost state into the next occupant.
    /// </summary>
    [Test]
    public void CleanupDisconnectedPeer_WipesEveryPerPeerBoard()
    {
        // Establish state on every per-peer board for `subject`.
        identityBoard.Set(subject, "0xSUBJECT_WALLET");        // IdentityBoard — set in SetUp too, re-set here for clarity
        PublishSnapshot(subject, seq: 42);                     // SnapshotBoard
        profileBoard.Set(subject, 7);                          // ProfileBoard
        spatialGrid.Set(subject, new Vector3(10, 0, 10));      // SpatialGrid

        // Precondition: state is actually present.
        Assert.That(snapshotBoard.TryRead(subject, out _), Is.True);
        Assert.That(identityBoard.GetWalletIdByPeerIndex(subject), Is.EqualTo("0xSUBJECT_WALLET"));
        Assert.That(profileBoard.Get(subject), Is.EqualTo(7));
        Assert.That(spatialGrid.GetPeers(new Vector3(10, 0, 10)), Does.Contain(subject));

        // Transition subject to DISCONNECTING and advance past the cleanup timeout.
        peers[subject] = new PeerState(PeerConnectionState.DISCONNECTING)
        {
            TransportState = new PeerTransportState(ConnectionTime: 0, DisconnectionTime: 0),
        };
        timeProvider.MonotonicTime.Returns(6000u);
        simulation.SimulateTick(peers, tickCounter: 1);

        // Postcondition: every per-peer board is wiped.
        Assert.That(peers, Does.Not.ContainKey(subject));
        Assert.That(snapshotBoard.TryRead(subject, out _), Is.False,
            "SnapshotBoard not cleared on disconnect");
        Assert.That(identityBoard.GetWalletIdByPeerIndex(subject), Is.Null,
            "IdentityBoard not cleared on disconnect");
        Assert.That(identityBoard.TryGetPeerIndexByWallet("0xSUBJECT_WALLET", out _), Is.False,
            "IdentityBoard reverse-map not cleared on disconnect");
        Assert.That(profileBoard.Get(subject), Is.EqualTo(0),
            "ProfileBoard not reset on disconnect");
        HashSet<PeerIndex>? stillAtCell = spatialGrid.GetPeers(new Vector3(10, 0, 10));
        Assert.That(stillAtCell == null || !stillAtCell.Contains(subject), Is.True,
            "SpatialGrid not cleared on disconnect");
    }

    /// <summary>
    ///     Disconnecting peer B must not disturb observer O's view of peer A. Each subject's
    ///     view is keyed independently by PeerIndex in <c>observerViews</c>, and cleanup
    ///     operates on a single PeerIndex at a time. Pins this so the allocator refactor
    ///     (which touches CleanupDisconnectedPeer) can't accidentally over-cleanup.
    /// </summary>
    [Test]
    public void DisconnectingOnePeer_DoesNotAffectObserversViewOfAnotherPeer()
    {
        var otherSubject = new PeerIndex(2);
        PublishSnapshot(otherSubject, seq: 1);
        identityBoard.Set(otherSubject, "0xOTHER_WALLET");

        // Observer sees both subjects — establish views for both.
        SetVisibleSubjects(
            (subject, PeerViewSimulationTier.TIER_0),
            (otherSubject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // PlayerJoined for both

        Assert.That(simulation.observerViews[observer], Does.ContainKey(subject));
        Assert.That(simulation.observerViews[observer], Does.ContainKey(otherSubject));

        // `otherSubject` disconnects.
        peers[otherSubject] = new PeerState(PeerConnectionState.DISCONNECTING)
        {
            TransportState = new PeerTransportState(ConnectionTime: 0, DisconnectionTime: 0),
        };
        // Still keep `subject` visible so its view gets stamped normally.
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        timeProvider.MonotonicTime.Returns(6000u);
        simulation.SimulateTick(peers, tickCounter: 1);

        // Observer's view of `subject` is intact; only `otherSubject` was touched by cleanup.
        Assert.That(simulation.observerViews[observer], Does.ContainKey(subject),
            "observer's view of an unrelated subject must survive another peer's cleanup");
        Assert.That(snapshotBoard.TryRead(subject, out _), Is.True,
            "unrelated subject's snapshot must survive another peer's cleanup");
        Assert.That(identityBoard.GetWalletIdByPeerIndex(subject), Is.EqualTo("0xSUBJECT_WALLET"),
            "unrelated subject's identity must survive another peer's cleanup");
    }

    /// <summary>
    ///     The aliasing guard must NOT fire on every tick when the wallet is unchanged — it is a
    ///     defense against rare aliasing, not a per-tick drip of spurious <c>PlayerLeft</c>s.
    ///     Regression pin: anyone swapping the string comparison for something over-eager (e.g.
    ///     reference equality, interning assumptions) would break this immediately.
    /// </summary>
    [Test]
    public void AliasingGuard_DoesNotFire_WhenWalletUnchanged()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // consume PlayerJoined from initial entry

        // Walk many ticks with the same wallet — publish fresh snapshots so the simulation has
        // something to diff against; nothing about the wallet changes.
        for (uint tick = 1; tick <= 10; tick++)
        {
            PublishSnapshot(subject, seq: tick + 1);
            simulation.SimulateTick(peers, tickCounter: tick);
        }

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft), Is.False,
            "aliasing guard must not emit PlayerLeft while the wallet for this PeerIndex is unchanged");
    }

    /// <summary>
    ///     Wallet comparison in the aliasing guard is case-insensitive, matching
    ///     <see cref="IdentityBoard" />'s case-insensitive reverse-lookup. Upstream components
    ///     (MetaForge, clients) may normalize hex addresses to different cases; a case flip must
    ///     not be treated as a new player.
    /// </summary>
    [Test]
    public void AliasingGuard_IsCaseInsensitive()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Swap casing — same wallet semantically.
        identityBoard.Remove(subject);
        identityBoard.Set(subject, "0xsubject_wallet");
        PublishSnapshot(subject, seq: 2);

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft), Is.False,
            "case-only wallet changes must not trigger PlayerLeft — matches IdentityBoard's OrdinalIgnoreCase semantics");
    }

    /// <summary>
    ///     Pins the documented reconnect-without-rekey behavior: when a wallet reconnects at a
    ///     DIFFERENT PeerIndex (the normal allocator outcome), the observer emits
    ///     <c>PlayerJoined</c> for the new index but the view for the prior index remains intact
    ///     until the periodic stale-view sweep fires. This window is the documented ~10 s UX
    ///     blemish (see <c>CLAUDE.md</c> § "Worker-shard isolation rule") — if a future "fix"
    ///     tries to smooth it by introducing cross-worker rekey or synchronous cross-observer
    ///     cleanup, this test will fail and force the conversation.
    /// </summary>
    [Test]
    public void SameWalletReconnect_AtDifferentPeerIndex_FreshPlayerJoinedFires_StaleViewSurvivesUntilSweep()
    {
        PeerIndex firstSubject = subject; // PeerIndex(1), wallet "0xSUBJECT_WALLET"
        var secondSubject = new PeerIndex(2);
        const string wallet = "0xSUBJECT_WALLET";

        SetVisibleSubjects((firstSubject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        Assert.That(simulation.observerViews[observer], Does.ContainKey(firstSubject),
            "precondition: observer has view for first subject");

        // First session disconnects — full cleanup flow clears IdentityBoard etc.
        peers[firstSubject] = new PeerState(PeerConnectionState.DISCONNECTING)
        {
            TransportState = new PeerTransportState(ConnectionTime: 0, DisconnectionTime: 0),
        };
        SetVisibleSubjects();
        timeProvider.MonotonicTime.Returns(6000u);
        simulation.SimulateTick(peers, tickCounter: 1);
        DrainAllMessages();

        // Second session: same wallet, DIFFERENT PeerIndex (the allocator issues a fresh slot
        // because the prior one is still in pending-recycle). The observer is unaware.
        identityBoard.Set(secondSubject, wallet);
        PublishSnapshot(secondSubject, seq: 1);
        SetVisibleSubjects((secondSubject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 2);

        List<OutgoingMessage> messages = DrainAllMessages();

        List<OutgoingMessage> joined = messages
           .Where(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined)
           .ToList();
        Assert.That(joined, Has.Count.EqualTo(1));
        Assert.That(joined[0].Message.PlayerJoined.State.SubjectId, Is.EqualTo(secondSubject.Value));
        Assert.That(joined[0].Message.PlayerJoined.UserId, Is.EqualTo(wallet));

        // Stale view for the prior PeerIndex is still present — awaiting SweepStaleViews.
        Assert.That(simulation.observerViews[observer], Does.ContainKey(firstSubject),
            "documented behavior: stale view persists until the periodic sweep — do not 'fix' without reading the rule");

        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft), Is.False,
            "no PlayerLeft yet — cleanup path is the sweep, not the new-session handshake");
    }

    /// <summary>
    ///     Pins the lockstep invariant between the simulation's cleanup and the allocator's
    ///     slot reuse: <c>Release</c> must be called from inside <c>CleanupDisconnectedPeer</c>,
    ///     not from a separate timer on the allocator side. Without this, the allocator's
    ///     grace clock and the simulation's cleanup clock drift relative to each other — a slot
    ///     can go back into the free-list before the per-peer boards (identity, snapshot, profile,
    ///     spatial) have been wiped, leaking stale state into whichever peer next takes the slot.
    /// </summary>
    [Test]
    public void Release_IsCalledFromCleanupDisconnectedPeer_NotBefore()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject transitions to DISCONNECTING — simulation must not release yet.
        peers[subject] = new PeerState(PeerConnectionState.DISCONNECTING)
        {
            TransportState = new PeerTransportState(ConnectionTime: 0, DisconnectionTime: 0),
        };

        // Short time advance: still inside the cleanup window.
        timeProvider.MonotonicTime.Returns(100u);
        simulation.SimulateTick(peers, tickCounter: 1);

        peerIndexAllocator.DidNotReceive().Release(subject);

        // Advance past the cleanup timeout — cleanup runs this tick, releasing the slot.
        timeProvider.MonotonicTime.Returns(6000u);
        simulation.SimulateTick(peers, tickCounter: 2);

        peerIndexAllocator.Received(1).Release(subject);
    }
}
