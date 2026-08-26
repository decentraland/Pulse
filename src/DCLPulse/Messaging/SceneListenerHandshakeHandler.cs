using DCL.Auth;
using Decentraland.Pulse;
using Google.Protobuf;
using Pulse.InterestManagement;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;

namespace Pulse.Messaging;

/// <summary>
///     Scene-listener handshake: authenticates via the shared <see cref="HandshakeHandlerBase" />
///     pipeline (identical attempt throttle, ban list, and replay guard), but the peer announces
///     a per-realm parcel-set AoI instead of an initial state and is never registered as a
///     subject — no SnapshotBoard slot, no SpatialGrid entry — so it stays invisible to every
///     player observer. The AoI is reassigned in place afterwards by
///     <see cref="SceneListenerUpdateHandler" />.
///     <para />
///     A listener is nevertheless a full peer holding a <see cref="PeerIndex" /> and a per-IP
///     connection slot, so this is also where the connection is moved out of the player budget of
///     <see cref="IpLimiter" /> and into the scene-listener one — see
///     <see cref="TryReserveListenerBudget" />.
/// </summary>
public class SceneListenerHandshakeHandler(MessagePipe messagePipe,
    AuthChainValidator authChainValidator,
    PeerStateFactory peerStateFactory,
    IdentityBoard identityBoard,
    ITransport transport,
    HandshakeAttemptPolicy attemptPolicy,
    PreAuthAdmission preAuthAdmission,
    HandshakeReplayPolicy replayPolicy,
    BanList banList,
    FieldValidator fieldValidator,
    SceneListenerCellMapper cellMapper,
    IpLimiter ipLimiter,
    ILogger<SceneListenerHandshakeHandler> logger)
    : HandshakeHandlerBase(messagePipe, authChainValidator, peerStateFactory, identityBoard, transport,
        attemptPolicy, preAuthAdmission, replayPolicy, banList, logger)
{
    protected override string LogName => "Scene-listener handshake";

    protected override ByteString GetAuthChain(ClientMessage message) => message.SceneListenerHandshake.AuthChain;

    /// <summary>
    ///     Invariant: a listener is never a subject. Only a peer still in PENDING_AUTH may become
    ///     one. Without this gate an already-AUTHENTICATED player could convert itself in place
    ///     (duplicate-session eviction never fires since duplicatedPeer == from), leaving its live
    ///     SnapshotBoard slot + SpatialGrid entry as a frozen ghost avatar. Also closes the
    ///     PENDING_DISCONNECT resurrection window.
    /// </summary>
    protected override bool CanBeginHandshake(PeerState existingState) =>
        existingState.ConnectionState == PeerConnectionState.PENDING_AUTH;

    /// <summary>
    ///     Field validation first, then the scene-listener connection budget — a malformed
    ///     announcement must not spend listener capacity on its way to being rejected. Both gates
    ///     run before the pipeline publishes the peer, so a refusal here never reaches AUTHENTICATED.
    /// </summary>
    protected override bool TryAuthorize(PeerIndex from, PeerState existingState, ClientMessage message, PeerState peer)
    {
        SceneListenerHandshakeRequest request = message.SceneListenerHandshake;

        if (!fieldValidator.ValidateSceneListenerHandshake(from, existingState, request,
                out Dictionary<string, HashSet<int>>? parcelsByRealm))
            return false;

        if (!TryReserveListenerBudget(from, existingState))
            return false;

        peer.SceneListener = new SceneListenerState(parcelsByRealm, cellMapper.ComputeCellKeys(parcelsByRealm));
        return true;
    }

    /// <summary>
    ///     Moves this peer's per-IP connection slot from the player budget it connected under into
    ///     the scene-listener budget, which is the first moment the server knows the connection is a
    ///     listener. On refusal nothing was mutated — the peer is still player-classed, so the
    ///     ordinary Disconnected release frees the budget that actually holds it — and the peer goes
    ///     down with <see cref="DisconnectReason.SCENE_LISTENER_IP_LIMIT_EXCEEDED" /> instead of
    ///     <see cref="DisconnectReason.IP_CONNECTION_LIMIT_EXCEEDED" /> so an operator sees which of
    ///     the two caps to raise. The refusal counter is bumped inside the limiter, tagged with the
    ///     class that refused.
    /// </summary>
    private bool TryReserveListenerBudget(PeerIndex from, PeerState existingState)
    {
        if (ipLimiter.TryReclassify(from, ConnectionClass.SCENE_LISTENER))
            return true;

        logger.LogWarning(
            "Scene-listener handshake rejected for peer {Peer}: the source IP is at its scene-listener connection cap.",
            from);

        return RejectHandshake(from, existingState, DisconnectReason.SCENE_LISTENER_IP_LIMIT_EXCEEDED);
    }

    protected override void OnAuthenticated(PeerIndex from, PeerState peer, ClientMessage message) =>
        PulseMetrics.SceneListener.CONNECTED.Add(1);

    protected override void LogAccepted(PeerIndex from, PeerState peer)
    {
        SceneListenerState listener = peer.SceneListener!;

        logger.LogInformation("Scene listener accepted with wallet {Wallet} - peerId {Peer} ({ParcelCount} parcels across realms {Realms})",
            peer.WalletId, from, listener.ParcelCount, string.Join(", ", listener.ParcelsByRealm.Keys));
    }
}
