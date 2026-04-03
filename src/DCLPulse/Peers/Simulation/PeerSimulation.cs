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
    /// <summary>
    ///     Sweep stale views every N ticks to reclaim memory from subjects that left the interest set.
    /// </summary>
    private const uint SWEEP_INTERVAL = 100;
    private const uint PEER_DISCONNECTION_CLEAN_TIMEOUT = 5000;
    private const uint PEER_PENDING_AUTH_CLEAN_TIMEOUT = 30000;

    public const string SELF_MIRROR_WALLET_ID = "self_mirror";

    private readonly IAreaOfInterest areaOfInterest;
    private readonly SnapshotBoard snapshotBoard;
    private readonly SpatialGrid spatialGrid;
    private readonly IdentityBoard identityBoard;
    private readonly MessagePipe messagePipe;
    private readonly ITimeProvider timeProvider;
    private readonly ITransport transport;
    private readonly ProfileBoard profileBoard;
    private readonly EmoteBoard emoteBoard;
    private readonly ILogger<PeerSimulation> logger;
    private readonly bool selfMirrorEnabled;
    private readonly PeerViewSimulationTier selfMirrorTier;

    /// <summary>
    ///     Per-observer views: observer PeerIndex → (subject PeerIndex → view).
    ///     Stored here, exclusive to this worker — no locks.
    /// </summary>
    internal readonly Dictionary<PeerIndex, Dictionary<PeerIndex, PeerToPeerView>> observerViews = new ();

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
        EmoteBoard emoteBoard,
        ILogger<PeerSimulation> logger,
        bool selfMirrorEnabled = false,
        int selfMirrorTier = 0)
    {
        this.areaOfInterest = areaOfInterest;
        this.snapshotBoard = snapshotBoard;
        this.spatialGrid = spatialGrid;
        this.identityBoard = identityBoard;
        this.messagePipe = messagePipe;
        this.timeProvider = timeProvider;
        this.transport = transport;
        this.profileBoard = profileBoard;
        this.emoteBoard = emoteBoard;
        this.logger = logger;
        this.selfMirrorEnabled = selfMirrorEnabled;
        this.selfMirrorTier = new PeerViewSimulationTier((byte)selfMirrorTier);

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
                    // Trigger disconnection flow which will mark the peer as DISCONNECTING and eventually removed
                    transport.Disconnect(observerId, DisconnectReason.AUTH_TIMEOUT);
                    logger.LogInformation("Peer {Peer} disconnected due to authentication timed out", observerId);
                    continue;
                }
            }

            if (observerState.ConnectionState == PeerConnectionState.DISCONNECTING)
            {
                // Remove the peer from the registry after time has passed from disconnection event
                if (timeProvider.MonotonicTime - observerState.TransportState.DisconnectionTime >= PEER_DISCONNECTION_CLEAN_TIMEOUT)
                {
                    snapshotBoard.ClearActive(observerId);
                    spatialGrid.Remove(observerId);
                    identityBoard.Remove(observerId);
                    profileBoard.Remove(observerId);
                    emoteBoard.Remove(observerId);
                    observerViews.Remove(observerId);
                    peersToBeRemoved.Add(observerId);
                    logger.LogInformation("Peer {Peer} removed after disconnected", observerId);
                    continue;
                }
            }

            if (observerState.ConnectionState != PeerConnectionState.AUTHENTICATED)
                continue;

            // Completes the emote in case no observer is near this peer
            emoteBoard.TryComplete(observerId, timeProvider.MonotonicTime);

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

            if (!snapshotBoard.TryRead(entry.Subject, out PeerSnapshot subjectSnapshot))
                continue;

            // NOTE: if a subject leaves the interest set briefly and re-enters before
            // the sweep (100 ticks), the old view survives. We send a STATE_DELTA from
            // the last-sent baseline — correct per sliding window, but the client has been
            // extrapolating during the gap. Consider sending STATE_FULL on re-entry if
            // the gap matters for client-side prediction quality.
            if (isNew)
            {
                resyncRequests?.Remove(entry.Subject);
                view = new PeerToPeerView { Onto = entry.Subject };

                int profileVersion = profileBoard.Get(entry.Subject);

                string? userId = isSelfMirror
                    ? SELF_MIRROR_WALLET_ID
                    : identityBoard.GetWalletIdByPeerIndex(entry.Subject);

                messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
                {
                    PlayerJoined = new PlayerJoined
                    {
                        UserId = userId,
                        ProfileVersion = profileVersion,
                        State = CreateFullState(entry.Subject, subjectSnapshot),
                    },
                }, PacketMode.RELIABLE));

                logger.LogInformation("Sending PlayerJoined for subject {Subject} to observer {Observer}", entry.Subject, observerId);

                view.LastSentProfileVersion = profileVersion;
                // Player joined message replaces the teleport message
                view.LastSentTeleportSeq = subjectSnapshot.Seq;
            }
            else
            {
                // Only announce on delta because PlayerJoined is considered as an announcement
                TryAnnounceProfile();

                if (subjectSnapshot.IsTeleport && view.LastSentTeleportSeq < subjectSnapshot.Seq)
                {
                    // Clear the resync since the teleport has the full player state and can fulfill it
                    resyncRequests?.Remove(entry.Subject);

                    messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
                    {
                        Teleported = new TeleportPerformed
                        {
                            SubjectId = entry.Subject,
                            Sequence = subjectSnapshot.Seq,
                            ServerTick = subjectSnapshot.ServerTick,
                            State = CreatePlayerState(subjectSnapshot),
                        }
                    }, PacketMode.RELIABLE));

                    view.LastSentTeleportSeq = subjectSnapshot.Seq;

                    logger.LogInformation("Broadcasting teleport from {Subject} to {ObserverId} at {Position}",
                        entry.Subject, observerId, subjectSnapshot.GlobalPosition);
                }
                else if (resyncRequests != null && resyncRequests.Remove(entry.Subject, out uint lastKnownSeq))
                {
                    // Try a targeted delta from the client's baseline; fall back to full state
                    // if the baseline is evicted, the seq hasn't advanced, or all fields are within epsilon.
                    if (!snapshotBoard.TryRead(entry.Subject, lastKnownSeq, out PeerSnapshot knownSnapshot)
                        || !SendDelta(knownSnapshot, PacketMode.RELIABLE))
                    {
                        messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
                        {
                            PlayerStateFull = CreateFullState(entry.Subject, subjectSnapshot),
                        }, PacketMode.RELIABLE));

                        logger.LogInformation("Resync fallback to STATE_FULL for subject {Subject} to observer {Observer} (lastKnownSeq={LastKnownSeq})",
                            entry.Subject, observerId, lastKnownSeq);
                    }
                    else
                    {
                        logger.LogInformation("Resync fulfilled with targeted delta for subject {Subject} to observer {Observer} (lastKnownSeq={LastKnownSeq})",
                            entry.Subject, observerId, lastKnownSeq);
                    }
                }
                else
                    SendDelta(view.LastSentSnapshot, PacketMode.UNRELIABLE_SEQUENCED);
            }

            SyncEmoteState();

            view.LastSentSeq = subjectSnapshot.Seq;
            view.LastSentSnapshot = subjectSnapshot;
            view.LastSeenTick = tickCounter;
            views[entry.Subject] = view;

            continue;

            void TryAnnounceProfile()
            {
                int currentVersion = profileBoard.Get(entry.Subject);

                if (currentVersion != view.LastSentProfileVersion)
                {
                    messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
                    {
                        PlayerProfileVersionAnnounced = new PlayerProfileVersionsAnnounced
                        {
                            Version = currentVersion,
                            SubjectId = entry.Subject,
                        },
                    }, PacketMode.RELIABLE));

                    logger.LogDebug("Profile version announced for subject {Subject} to observer {Observer} (v{PrevVersion} -> v{Version})",
                        entry.Subject, observerId, view.LastSentProfileVersion, currentVersion);

                    view.LastSentProfileVersion = currentVersion;
                }
            }

            void SyncEmoteState()
            {
                // If the emote completion has not been processed by the peer's worker yet, try to complete it now
                emoteBoard.TryComplete(entry.Subject, timeProvider.MonotonicTime);

                EmoteState? emoteState = emoteBoard.Get(entry.Subject);
                string? currentEmote = emoteState?.EmoteId;

                if (currentEmote == view.LastSentEmoteId)
                    return;

                if (currentEmote != null)
                {
                    messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
                    {
                        EmoteStarted = new EmoteStarted
                        {
                            SubjectId = entry.Subject.Value,
                            ServerTick = emoteState!.StartTick,
                            EmoteId = currentEmote,
                            PlayerState = CreatePlayerState(subjectSnapshot),
                        },
                    }, PacketMode.RELIABLE));

                    logger.LogInformation("Broadcasting EmoteStarted {EmoteId} for subject {Subject} to observer {Observer}",
                        currentEmote, entry.Subject, observerId);
                }
                else if (emoteState is { StopTick: not null, StopReason: not null })
                {
                    messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
                    {
                        EmoteStopped = new EmoteStopped
                        {
                            SubjectId = entry.Subject.Value,
                            ServerTick = emoteState.StopTick.Value,
                            Reason = emoteState.StopReason.Value,
                        },
                    }, PacketMode.RELIABLE));

                    logger.LogInformation("Sending EmoteStopped for subject {Subject} to observer {Observer} (reason={Reason})",
                        entry.Subject, observerId, emoteState.StopReason.Value);
                }

                view.LastSentEmoteId = currentEmote;
            }

            bool SendDelta(PeerSnapshot baseline, PacketMode packetMode)
            {
                if (baseline.Seq == subjectSnapshot.Seq)
                    return false;

                PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(entry.Subject, baseline, subjectSnapshot, entry.Tier);

                messagePipe.Send(new OutgoingMessage(observerId, new ServerMessage
                {
                    PlayerStateDelta = delta,
                }, packetMode));

                return true;
            }
        }
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
