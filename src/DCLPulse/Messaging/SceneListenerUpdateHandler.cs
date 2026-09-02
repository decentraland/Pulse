using Decentraland.Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Peers;

namespace Pulse.Messaging;

/// <summary>
///     Reassigns a scene listener's area of interest in place. The peer keeps its connection and
///     identity; only the announced realms and their parcels are replaced, by swapping in a fresh
///     <see cref="SceneListenerState" />. The simulation reads that descriptor afresh every tick,
///     so the new set takes effect on the next one: subjects inside it are picked up and joined
///     like any newly visible peer, and subjects the listener has dropped simply stop being
///     collected — their views age out through the ordinary stale-view sweep, exactly as they do
///     for a player who walks out of range.
///     <para />
///     Runs on the owning worker thread, the same one that runs this peer's simulation tick, so the
///     swap can never be observed half-applied.
/// </summary>
public class SceneListenerUpdateHandler(ILogger<SceneListenerUpdateHandler> logger,
    DiscreteEventRateLimiter rateLimiter,
    FieldValidator fieldValidator)
    : RuntimePacketHandlerBase<SceneListenerUpdateHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out PeerState? peerState))
            return;

        // Only a peer that authenticated as a listener has an AoI to replace. A player sending
        // this is a client bug, not an attack surface — drop it before the rate limiter so it
        // cannot spend a player's discrete-event budget, and count it rather than warn.
        if (peerState.SceneListener is not { } previous)
        {
            PulseMetrics.SceneListener.FORBIDDEN_MESSAGES_DROPPED.Add(1);

            // Dropping ahead of the limiter leaves nothing throttling this path, so the counter
            // above must be all it costs: unguarded, the argument array and the boxed PeerIndex
            // are built at the call site on every packet even with Debug off.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Peer {Peer} sent SceneListenerUpdate but is not a scene listener, dropped", from);

            return;
        }

        // Expansion is O(Σ rect area) and recomputes the covering cells, so it rides the same
        // token bucket as the other discrete events.
        if (!rateLimiter.TryAccept(from, peerState))
            return;

        if (!fieldValidator.ValidateSceneListenerUpdate(from, peerState, message.SceneListenerUpdate,
                out SceneListenerState? updated))
            return;

        // Replacing the whole descriptor rather than mutating it keeps SceneListenerState
        // immutable for its readers. Realms absent from the update are simply no longer observed.
        peerState.SceneListener = updated;

        logger.LogInformation(
            "Scene listener {Peer} reassigned its AoI: {ParcelCount} parcels across {RealmCount} realms (was {PreviousParcelCount} across {PreviousRealmCount})",
            from, updated.ParcelCount, updated.ParcelsByRealm.Count, previous.ParcelCount, previous.ParcelsByRealm.Count);

        // The budget admits hundreds of realms, so joining their names is unbounded work — it goes
        // behind a level check rather than into an argument the runtime evaluates regardless.
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Scene listener {Peer} now observes realms {Realms}", from, string.Join(", ", updated.ParcelsByRealm.Keys));
    }
}
