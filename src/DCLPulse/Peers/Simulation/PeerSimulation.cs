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
public sealed class PeerSimulation
{
    /// <summary>
    ///     Sweep stale views every N ticks to reclaim memory from subjects that left the interest set.
    /// </summary>
    private const uint SWEEP_INTERVAL = 100;
    private const uint PEER_DISCONNECTION_CLEAN_TIMEOUT = 5000;

    private readonly IAreaOfInterest areaOfInterest;
    private readonly SnapshotBoard snapshotBoard;
    private readonly MessagePipe messagePipe;
    private readonly uint[] simulationSteps;
    private readonly ITimeProvider timeProvider;

    /// <summary>
    ///     Per-observer views: observer PeerIndex → (subject PeerIndex → view).
    ///     Stored here, exclusive to this worker — no locks.
    /// </summary>
    private readonly Dictionary<PeerIndex, Dictionary<PeerIndex, PeerToPeerView>> observerViews = new ();

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
        MessagePipe messagePipe,
        uint[] simulationSteps,
        ITimeProvider timeProvider)
    {
        this.areaOfInterest = areaOfInterest;
        this.snapshotBoard = snapshotBoard;
        this.messagePipe = messagePipe;
        this.simulationSteps = simulationSteps;
        this.timeProvider = timeProvider;

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
            if (observerState.ConnectionState == PeerConnectionState.DISCONNECTING)
            {
                // Remove the peer from the registry after time has passed from disconnection event
                if (timeProvider.MonotonicTime - observerState.TransportState.DisconnectionTime >= PEER_DISCONNECTION_CLEAN_TIMEOUT)
                {
                    // TODO: clean snapshots
                    peersToBeRemoved.Add(observerId);
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

            ProcessVisibleSubjects(observerId, views, tickCounter);

            if (tickCounter % SWEEP_INTERVAL == 0)
                SweepStaleViews(views, tickCounter);
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
        uint tickCounter)
    {
        for (var i = 0; i < collector.Count; i++)
        {
            InterestEntry entry = collector.Entries[i];

            if (entry.Subject == observerId)
                continue;

            int tierIndex = entry.Tier.Value;

            // Stamp before tier gate — a TIER_2 subject fires every 4th tick,
            // but it's still visible on the intervening ticks. Without this,
            // 3 unstamped ticks would trigger false re-entry detection.
            if (views.TryGetValue(entry.Subject, out PeerToPeerView view))
            {
                view.LastSeenTick = tickCounter;
                views[entry.Subject] = view;
            }

            // Skip if this tier is not due on this tick
            if (tierIndex < tierDivisors.Length && tickCounter % tierDivisors[tierIndex] != 0)
                continue;

            if (!snapshotBoard.TryRead(entry.Subject, out PeerSnapshot subjectSnapshot))
                continue;

            // NOTE: if a subject leaves the interest set briefly and re-enters before
            // the sweep (100 ticks), the old view survives. We send a STATE_DELTA from
            // the last-sent baseline — correct per sliding window, but the client has been
            // extrapolating during the gap. Consider sending STATE_FULL on re-entry if
            // the gap matters for client-side prediction quality.
            bool isNew = !views.ContainsKey(entry.Subject);

            if (isNew)
            {
                view = new PeerToPeerView { Onto = entry.Subject };
                SendStateFull(observerId, entry.Subject, subjectSnapshot);
            }
            else if (view.LastSentSeq != subjectSnapshot.Seq)
            {
                ServerMessage delta =
                    PeerViewDiff.CreateMessage(view.LastSentSnapshot, subjectSnapshot, entry.Tier);

                messagePipe.Send(new OutgoingMessage(observerId, delta, ITransport.PacketMode.UNRELIABLE_SEQUENCED));
            }
            else
            {
                // No new data — already stamped above, nothing else to do
                continue;
            }

            view.LastSentSeq = subjectSnapshot.Seq;
            view.LastSentSnapshot = subjectSnapshot;
            view.LastSeenTick = tickCounter;
            views[entry.Subject] = view;
        }
    }

    /// <summary>
    ///     Periodic sweep — removes views not touched in recent ticks. Runs every <see cref="SWEEP_INTERVAL" /> ticks
    ///     to reclaim memory from subjects that left the interest set. Not on the hot path.
    /// </summary>
    private void SweepStaleViews(Dictionary<PeerIndex, PeerToPeerView> views, uint tickCounter)
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
            views.Remove(id);
    }

    private void SendStateFull(PeerIndex observerId, PeerIndex subjectId, PeerSnapshot snapshot)
    {
        // TODO: Construct PlayerStateFull from snapshot and send reliably
    }
}
