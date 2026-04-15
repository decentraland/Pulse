using Decentraland.Common;
using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Transport;
using static Pulse.Messaging.MessagePipe;

namespace Pulse.Peers.Simulation;

/// <summary>
///     Per-worker simulation step. Iterates authenticated observers owned by this worker,
///     queries <see cref="IAreaOfInterest" /> for visible subjects, diffs snapshots,
///     and sends STATE_DELTA / STATE_FULL via <see cref="MessagePipe" />.
///     Instantiated once per worker - not shared across workers: thus thread-safety is ensured without concurrency.
/// </summary>
public sealed class PeerSimulation : IPeerSimulation
{
    public const string SELF_MIRROR_WALLET_ID = "self_mirror";
    /// <summary>
    ///     Sweep stale views every N ticks to reclaim memory from subjects that left the interest set.
    /// </summary>
    private const uint SWEEP_INTERVAL = 100;
    private const uint PEER_DISCONNECTION_CLEAN_TIMEOUT = 5000;
    private const uint PEER_PENDING_AUTH_CLEAN_TIMEOUT = 30000;

    /// <summary>
    ///     Per-observer views: observer PeerIndex → (subject PeerIndex → view).
    ///     Stored here, exclusive to this worker — no locks.
    /// </summary>
    internal readonly Dictionary<PeerIndex, Dictionary<PeerIndex, PeerToPeerView>> observerViews = new ();

    private readonly IAreaOfInterest areaOfInterest;
    private readonly SnapshotBoard snapshotBoard;
    private readonly SpatialGrid spatialGrid;
    private readonly IdentityBoard identityBoard;
    private readonly MessagePipe messagePipe;
    private readonly ITimeProvider timeProvider;
    private readonly ITransport transport;
    private readonly ProfileBoard profileBoard;
    private readonly ILogger<PeerSimulation> logger;
    private readonly bool selfMirrorEnabled;
    private readonly PeerViewSimulationTier selfMirrorTier;
    private readonly bool resyncWithDelta;

    /// <summary>
    ///     Reusable collector to avoid allocation per tick.
    /// </summary>
    private readonly InterestCollector collector = new ();

    /// <summary>
    ///     Pre-computed divisors: SimulationSteps[tier] / baseTickMs.
    ///     TIER_0 → 1 (every tick), TIER_1 → 2 (every 2nd tick), TIER_2 → 4 (every 4th tick).
    /// </summary>
    private readonly uint[] tierDivisors;

    /// <summary>
    ///     Reusable buffer for sweep removals — avoids allocating a list every sweep.
    /// </summary>
    private readonly List<PeerIndex> sweepBuffer = new ();

    private readonly HashSet<PeerIndex> peersToBeRemoved = new ();

    public uint BaseTickMs { get; }

    public PeerSimulation(
        IAreaOfInterest areaOfInterest,
        SnapshotBoard snapshotBoard,
        SpatialGrid spatialGrid,
        IdentityBoard identityBoard,
        MessagePipe messagePipe,
        uint[] simulationSteps,
        ITimeProvider timeProvider,
        ITransport transport,
        ProfileBoard profileBoard,
        ILogger<PeerSimulation> logger,
        bool selfMirrorEnabled = false,
        int selfMirrorTier = 0,
        bool resyncWithDelta = false)
    {
        this.areaOfInterest = areaOfInterest;
        this.snapshotBoard = snapshotBoard;
        this.spatialGrid = spatialGrid;
        this.identityBoard = identityBoard;
        this.messagePipe = messagePipe;
        this.timeProvider = timeProvider;
        this.transport = transport;
        this.profileBoard = profileBoard;
        this.logger = logger;
        this.selfMirrorEnabled = selfMirrorEnabled;
        this.selfMirrorTier = new PeerViewSimulationTier((byte)selfMirrorTier);
        this.resyncWithDelta = resyncWithDelta;

        BaseTickMs = simulationSteps[0];
        tierDivisors = new uint[simulationSteps.Length];

        for (var i = 0; i < simulationSteps.Length; i++)
            tierDivisors[i] = simulationSteps[i] / BaseTickMs;
    }

    /// <summary>
    ///     Runs one simulation tick for all authenticated observers in the given peer set.
    /// </summary>
    public void SimulateTick(Dictionary<PeerIndex, PeerState> peers, uint tickCounter)
    {
        foreach ((PeerIndex observerId, PeerState observerState) in peers)
        {
            if (observerState.ConnectionState == PeerConnectionState.PENDING_AUTH)
            {
                if (timeProvider.MonotonicTime - observerState.TransportState.ConnectionTime >= PEER_PENDING_AUTH_CLEAN_TIMEOUT)
                {
                    transport.Disconnect(observerId, DisconnectReason.AUTH_TIMEOUT);
                    logger.LogInformation("Peer {Peer} disconnected due to authentication timed out", observerId);
                    continue;
                }
            }

            if (observerState.ConnectionState == PeerConnectionState.DISCONNECTING)
            {
                if (timeProvider.MonotonicTime - observerState.TransportState.DisconnectionTime >= PEER_DISCONNECTION_CLEAN_TIMEOUT)
                {
                    CleanupDisconnectedPeer(observerId);
                    continue;
                }
            }

            if (observerState.ConnectionState != PeerConnectionState.AUTHENTICATED)
                continue;

            if (!snapshotBoard.TryRead(observerId, out PeerSnapshot observerSnapshot))
                continue;

            if (!observerViews.TryGetValue(observerId, out Dictionary<PeerIndex, PeerToPeerView>? views))
            {
                views = new Dictionary<PeerIndex, PeerToPeerView>();
                observerViews[observerId] = views;
            }

            collector.Clear();
            areaOfInterest.GetVisibleSubjects(observerId, in observerSnapshot, collector);

            if (selfMirrorEnabled)
                collector.Add(observerId, selfMirrorTier);

            ProcessVisibleSubjects(observerId, views, observerState.ResyncRequests, tickCounter);

            observerState.ResyncRequests?.Clear();

            if (tickCounter % SWEEP_INTERVAL == 0)
                SweepStaleViews(observerId, views, tickCounter);
        }

        foreach (PeerIndex pi in peersToBeRemoved)
            peers.Remove(pi);

        peersToBeRemoved.Clear();
    }

    /// <summary>
    ///     Call when a peer disconnects to clean up its observer views.
    /// </summary>
    public void RemoveObserver(PeerIndex observerId)
    {
        observerViews.Remove(observerId);
    }

    // ── Per-subject orchestration ───────────────────────────────────

    private void ProcessVisibleSubjects(
        PeerIndex observerId,
        Dictionary<PeerIndex, PeerToPeerView> views,
        Dictionary<PeerIndex, uint>? resyncRequests,
        uint tickCounter)
    {
        for (var i = 0; i < collector.Count; i++)
        {
            InterestEntry entry = collector.Entries[i];

            bool isSelfMirror = entry.Subject == observerId;

            if (isSelfMirror && !selfMirrorEnabled)
                continue;

            bool isNew = !views.TryGetValue(entry.Subject, out PeerToPeerView view);

            // Stamp before tier gate — a TIER_2 subject fires every 4th tick,
            // but it's still visible on the intervening ticks. Without this,
            // 3 unstamped ticks would trigger false re-entry detection.
            if (!isNew)
            {
                view.LastSeenTick = tickCounter;
                views[entry.Subject] = view;
            }

            // Skip if this tier is not due on this tick — but never gate a pending resync
            bool hasResync = !isNew && resyncRequests != null && resyncRequests.ContainsKey(entry.Subject);
            int tierIndex = entry.Tier.Value;

            if (!hasResync && tierIndex < tierDivisors.Length && tickCounter % tierDivisors[tierIndex] != 0)
                continue;

            if (!snapshotBoard.TryRead(entry.Subject, out PeerSnapshot latestSnapshot))
                continue;

            if (isNew)
            {
                view = HandleNewSubject(observerId, entry.Subject, latestSnapshot, isSelfMirror, resyncRequests);
                view.LastSeenTick = tickCounter;
                views[entry.Subject] = view;
                continue;
            }

            TryAnnounceProfile(observerId, entry.Subject, ref view);

            PeerSnapshot lastSentState = ProcessExistingSubject(
                observerId, entry, ref view, latestSnapshot, resyncRequests);

            view.LastSentSnapshot = lastSentState;
            view.LastSeenTick = tickCounter;
            views[entry.Subject] = view;
        }
    }

    /// <summary>
    ///     First-time visibility: send PlayerJoined with full state.
    /// </summary>
    private PeerToPeerView HandleNewSubject(
        PeerIndex observerId, PeerIndex subjectId,
        PeerSnapshot latestSnapshot, bool isSelfMirror,
        Dictionary<PeerIndex, uint>? resyncRequests)
    {
        resyncRequests?.Remove(subjectId);

        int profileVersion = profileBoard.Get(subjectId);

        string? userId = isSelfMirror
            ? SELF_MIRROR_WALLET_ID
            : identityBoard.GetWalletIdByPeerIndex(subjectId);

        messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
        {
            PlayerJoined = new PlayerJoined
            {
                UserId = userId,
                ProfileVersion = profileVersion,
                State = CreateFullState(subjectId, latestSnapshot),
            },
        }, PacketMode.RELIABLE));

        logger.LogInformation("Sending PlayerJoined for subject {Subject} to observer {Observer}", subjectId, observerId);

        return new PeerToPeerView
        {
            Onto = subjectId,
            LastSentProfileVersion = profileVersion,
            LastSentTeleportSeq = latestSnapshot.Seq,
            LastSentSnapshot = latestSnapshot,
            LastSentSeq = latestSnapshot.Seq,
        };
    }

    /// <summary>
    ///     Processes an already-known subject: scans intermediates for discrete events,
    ///     syncs emote stop, then falls back to resync or delta.
    ///     Returns the snapshot that should become the new baseline.
    /// </summary>
    private PeerSnapshot ProcessExistingSubject(
        PeerIndex observerId,
        InterestEntry entry,
        ref PeerToPeerView view,
        PeerSnapshot latestSnapshot,
        Dictionary<PeerIndex, uint>? resyncRequests)
    {
        PeerSnapshot lastSentState = view.LastSentSnapshot;
        var discreteEventSent = false;

        // --- Phase 1: scan intermediates, collect last of each discrete event type ---
        ScanIntermediateEvents(entry.Subject, view.LastSentSnapshot.Seq, latestSnapshot.Seq,
            out PeerSnapshot? lastEmoteStart, out PeerSnapshot? lastEmoteStop, out PeerSnapshot? lastTeleport);

        // --- Broadcast teleport (spatial snap first) ---
        if (lastTeleport is { } tp && view.LastSentTeleportSeq < tp.Seq)
        {
            SendTeleport(observerId, ref view, entry.Subject, tp);
            resyncRequests?.Remove(entry.Subject);
            view.LastSentTeleportSeq = tp.Seq;
            lastSentState = tp;
            discreteEventSent = true;
        }

        // --- Broadcast emote start only if the emote is still active (not stopped in the same batch).
        //     An emote that started and stopped between ticks is invisible to the observer. ---
        bool emoteStartIsEffective = lastEmoteStart.HasValue
                                     && lastEmoteStart.Value.Seq > (lastEmoteStop?.Seq ?? 0);

        if (emoteStartIsEffective
            && lastEmoteStart!.Value.Emote is { EmoteId: not null } emote
            && !(emote.EmoteId == view.LastSentEmote?.EmoteId && emote.StartTick == view.LastSentEmote?.StartTick))
        {
            PeerSnapshot es = lastEmoteStart.Value;
            SendEmoteStarted(observerId, ref view, entry.Subject, es, emote);
            resyncRequests?.Remove(entry.Subject);
            view.LastSentEmote = emote;

            if (es.Seq > lastSentState.Seq)
                lastSentState = es;

            discreteEventSent = true;
        }

        // --- Phase 2: sync emote stop (skip when the start is still effective —
        //     either just sent, or already synced via dedup — the emote is active) ---
        if (!emoteStartIsEffective)
            TrySyncEmoteStop(observerId, entry.Subject, ref view, ref lastSentState, lastEmoteStop);

        // --- Phase 3: resync or delta (skip if discrete events already carried full state) ---
        if (!discreteEventSent)
        {
            lastSentState = HandleResyncOrDelta(
                observerId, entry, ref view, lastSentState, latestSnapshot, resyncRequests);
        }

        return lastSentState;
    }

    private void ScanIntermediateEvents(PeerIndex subjectId, uint fromSeq, uint toSeq,
        out PeerSnapshot? lastEmoteStart, out PeerSnapshot? lastEmoteStop, out PeerSnapshot? lastTeleport)
    {
        lastEmoteStart = null;
        lastEmoteStop = null;
        lastTeleport = null;

        for (uint seq = fromSeq + 1; seq <= toSeq; seq++)
        {
            if (!snapshotBoard.TryRead(subjectId, seq, out PeerSnapshot snapshot))
                continue;

            if (snapshot.Emote is { EmoteId: not null })
                lastEmoteStart = snapshot;

            if (snapshot.Emote is { StopReason: not null })
                lastEmoteStop = snapshot;

            if (snapshot.IsTeleport)
                lastTeleport = snapshot;
        }
    }

    // ── Emote stop detection ────────────────────────────────────────

    private void TrySyncEmoteStop(
        PeerIndex observerId, PeerIndex subjectId,
        ref PeerToPeerView view,
        ref PeerSnapshot lastSentState,
        PeerSnapshot? stopSnapshot)
    {
        if (view.LastSentEmote?.EmoteId == null)
            return;

        // Explicit stop — either Cancelled (from EmoteStopHandler) or Completed (from EmoteCompleter).
        // Both are published as real stop snapshots on the subject's worker, so they carry their own seq.
        if (stopSnapshot?.Emote is { StopReason: not null } stopEmote)
        {
            SendEmoteStopped(observerId, ref view, subjectId, stopSnapshot.Value, stopEmote.StopReason!.Value, stopSnapshot.Value.ServerTick);
            view.LastSentEmote = null;

            // Advance the Phase 3 baseline to the stop snapshot — otherwise Phase 3's
            // SendDelta would diff from the pre-emote baseline and potentially re-send
            // the same seq already carried by EmoteStopped above.
            if (stopSnapshot.Value.Seq > lastSentState.Seq)
                lastSentState = stopSnapshot.Value;
        }
    }

    // ── Resync / delta ──────────────────────────────────────────────

    private PeerSnapshot HandleResyncOrDelta(
        PeerIndex observerId,
        InterestEntry entry,
        ref PeerToPeerView view,
        PeerSnapshot lastSentState,
        PeerSnapshot latestSnapshot,
        Dictionary<PeerIndex, uint>? resyncRequests)
    {
        if (resyncRequests == null || !resyncRequests.Remove(entry.Subject, out uint lastKnownSeq))
        {
            SendDelta(observerId, ref view, entry.Subject, lastSentState, latestSnapshot, entry.Tier, PacketMode.UNRELIABLE_SEQUENCED);
            return latestSnapshot;
        }

        // Try a targeted delta from the client's baseline; fall back to full state
        // if the baseline is evicted, the seq hasn't advanced, or the feature is disabled.
        if (resyncWithDelta
            && snapshotBoard.TryRead(entry.Subject, lastKnownSeq, out PeerSnapshot knownSnapshot)
            && knownSnapshot.Seq != latestSnapshot.Seq)
        {
            SendDelta(observerId, ref view, entry.Subject, knownSnapshot, latestSnapshot, entry.Tier, PacketMode.RELIABLE);

            logger.LogInformation("Resync fulfilled with targeted delta for subject {Subject} to observer {Observer} (lastKnownSeq={LastKnownSeq})",
                entry.Subject, observerId, lastKnownSeq);
        }
        else
        {
            SendTracked(observerId, ref view, latestSnapshot.Seq, new ServerMessage
            {
                PlayerStateFull = CreateFullState(entry.Subject, latestSnapshot),
            }, PacketMode.RELIABLE);

            logger.LogWarning("Resync fallback to STATE_FULL for subject {Subject} to observer {Observer} (lastKnownSeq={LastKnownSeq}, gap={SeqGap})",
                entry.Subject, observerId, lastKnownSeq, latestSnapshot.Seq - lastKnownSeq);
        }

        return latestSnapshot;
    }

    // ── Message sending ─────────────────────────────────────────────

    /// <summary>
    ///     Sends a seq-carrying message to the observer and records the seq on the view as a
    ///     duplicate-delivery tripwire. Logs an error if <paramref name="seq" /> equals
    ///     <see cref="PeerToPeerView.LastSentSeq" /> — that means the same sequence number
    ///     has already been delivered to this observer for this subject, which indicates a
    ///     bug in the simulation pipeline.
    /// </summary>
    private void SendTracked(PeerIndex observerId, ref PeerToPeerView view, uint seq, ServerMessage message, PacketMode packetMode)
    {
        if (seq == view.LastSentSeq)
            logger.LogError(
                "Duplicate seq {Seq} sent to observer {Observer} for subject {Subject} ({MessageCase})",
                seq, observerId, view.Onto, message.MessageCase);

        view.LastSentSeq = seq;
        messagePipe.Send(new OutgoingMessage(observerId, message, packetMode));
    }

    private void SendTeleport(PeerIndex observerId, ref PeerToPeerView view, PeerIndex subjectId, PeerSnapshot snapshot)
    {
        SendTracked(observerId, ref view, snapshot.Seq, new ServerMessage
        {
            Teleported = new TeleportPerformed
            {
                SubjectId = subjectId,
                Sequence = snapshot.Seq,
                ServerTick = snapshot.ServerTick,
                State = CreatePlayerState(snapshot),
            },
        }, PacketMode.RELIABLE);

        logger.LogInformation("Broadcasting teleport from {Subject} to {ObserverId} at {Position}",
            subjectId, observerId, snapshot.GlobalPosition);
    }

    private void SendEmoteStarted(PeerIndex observerId, ref PeerToPeerView view, PeerIndex subjectId, PeerSnapshot snapshot, EmoteState emote)
    {
        SendTracked(observerId, ref view, snapshot.Seq, new ServerMessage
        {
            EmoteStarted = new EmoteStarted
            {
                SubjectId = subjectId.Value,
                Sequence = snapshot.Seq,
                ServerTick = emote.StartTick,
                EmoteId = emote.EmoteId,
                PlayerState = CreatePlayerState(snapshot),
            },
        }, PacketMode.RELIABLE);

        logger.LogInformation("Broadcasting EmoteStarted {EmoteId} for subject {Subject} to observer {Observer}",
            emote.EmoteId, subjectId, observerId);
    }

    private void SendEmoteStopped(PeerIndex observerId, ref PeerToPeerView view, PeerIndex subjectId, PeerSnapshot snapshot, EmoteStopReason reason,
        uint serverTick)
    {
        SendTracked(observerId, ref view, snapshot.Seq, new ServerMessage
        {
            EmoteStopped = new EmoteStopped
            {
                SubjectId = subjectId.Value,
                ServerTick = serverTick,
                Reason = reason,
                Sequence = snapshot.Seq,
                PlayerState = CreatePlayerState(snapshot),
            },
        }, PacketMode.RELIABLE);

        logger.LogInformation("Sending EmoteStopped for subject {Subject} to observer {Observer} (reason={Reason})",
            subjectId, observerId, reason);
    }

    private void SendDelta(PeerIndex observerId, ref PeerToPeerView view, PeerIndex subjectId, PeerSnapshot baseline, PeerSnapshot target,
        PeerViewSimulationTier tier,
        PacketMode packetMode)
    {
        if (baseline.Seq == target.Seq)
            return;

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(subjectId, baseline, target, tier);

        SendTracked(observerId, ref view, target.Seq, new ServerMessage
        {
            PlayerStateDelta = delta,
        }, packetMode);
    }

    private void TryAnnounceProfile(PeerIndex observerId, PeerIndex subjectId, ref PeerToPeerView view)
    {
        int currentVersion = profileBoard.Get(subjectId);

        if (currentVersion != view.LastSentProfileVersion)
        {
            messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
            {
                PlayerProfileVersionAnnounced = new PlayerProfileVersionsAnnounced
                {
                    Version = currentVersion,
                    SubjectId = subjectId,
                },
            }, PacketMode.RELIABLE));

            logger.LogDebug("Profile version announced for subject {Subject} to observer {Observer} (v{PrevVersion} -> v{Version})",
                subjectId, observerId, view.LastSentProfileVersion, currentVersion);

            view.LastSentProfileVersion = currentVersion;
        }
    }

    // ── Cleanup ─────────────────────────────────────────────────────

    private void CleanupDisconnectedPeer(PeerIndex peerId)
    {
        snapshotBoard.ClearActive(peerId);
        spatialGrid.Remove(peerId);
        identityBoard.Remove(peerId);
        profileBoard.Remove(peerId);
        observerViews.Remove(peerId);
        peersToBeRemoved.Add(peerId);
        logger.LogInformation("Peer {Peer} removed after disconnected", peerId);
    }

    /// <summary>
    ///     Periodic sweep — removes views not touched in recent ticks. Runs every <see cref="SWEEP_INTERVAL" /> ticks
    ///     to reclaim memory from subjects that left the interest set. Not on the hot path.
    /// </summary>
    private void SweepStaleViews(PeerIndex observerId, Dictionary<PeerIndex, PeerToPeerView> views, uint tickCounter)
    {
        if (views.Count == 0)
            return;

        sweepBuffer.Clear();

        foreach ((PeerIndex subjectId, PeerToPeerView view) in views)
        {
            if (tickCounter - view.LastSeenTick > SWEEP_INTERVAL)
                sweepBuffer.Add(subjectId);
        }

        foreach (PeerIndex id in sweepBuffer)
        {
            messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
            {
                PlayerLeft = new PlayerLeft { SubjectId = id },
            }, PacketMode.RELIABLE));

            logger.LogInformation("Sending PlayerLeft for subject {Subject} to observer {Observer} (stale view swept)", id, observerId);

            views.Remove(id);
        }
    }

    // ── State conversion ────────────────────────────────────────────

    private PlayerStateFull CreateFullState(PeerIndex subjectId, PeerSnapshot snapshot) =>
        new ()
        {
            SubjectId = subjectId.Value,
            Sequence = snapshot.Seq,
            ServerTick = snapshot.ServerTick,
            State = CreatePlayerState(snapshot),
        };

    private static PlayerState CreatePlayerState(PeerSnapshot snapshot)
    {
        var state = new PlayerState
        {
            ParcelIndex = snapshot.Parcel,
            Position = new Vector3 { X = snapshot.LocalPosition.X, Y = snapshot.LocalPosition.Y, Z = snapshot.LocalPosition.Z },
            Velocity = new Vector3 { X = snapshot.Velocity.X, Y = snapshot.Velocity.Y, Z = snapshot.Velocity.Z },
            RotationY = snapshot.RotationY,
            MovementBlend = snapshot.MovementBlend,
            SlideBlend = snapshot.SlideBlend,
            StateFlags = (uint)snapshot.AnimationFlags,
            GlideState = snapshot.GlideState,
        };

        if (snapshot.HeadYaw.HasValue)
            state.HeadYaw = snapshot.HeadYaw.Value;

        if (snapshot.HeadPitch.HasValue)
            state.HeadPitch = snapshot.HeadPitch.Value;

        return state;
    }
}
