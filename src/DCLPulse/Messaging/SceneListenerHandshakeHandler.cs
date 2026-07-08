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
///     an immutable parcel-set AoI instead of an initial state and is never registered as a
///     subject — no SnapshotBoard slot, no SpatialGrid entry — so it stays invisible to every
///     player observer. Re-announcing the parcel set requires reconnecting: no post-auth message
///     can mutate it.
/// </summary>
public class SceneListenerHandshakeHandler(MessagePipe messagePipe,
    HandshakeAuthenticator authenticator,
    PeerStateFactory peerStateFactory,
    IdentityBoard identityBoard,
    ITransport transport,
    HandshakeAttemptPolicy attemptPolicy,
    PreAuthAdmission preAuthAdmission,
    HandshakeReplayPolicy replayPolicy,
    BanList banList,
    FieldValidator fieldValidator,
    SceneListenerCellMapper cellMapper,
    ILogger<SceneListenerHandshakeHandler> logger)
    : HandshakeHandlerBase(messagePipe, authenticator, peerStateFactory, identityBoard, transport,
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

    protected override bool TryAuthorize(PeerIndex from, PeerState existingState, ClientMessage message, PeerState peer)
    {
        SceneListenerHandshakeRequest request = message.SceneListenerHandshake;

        if (!fieldValidator.ValidateSceneListenerHandshake(from, existingState, request, out HashSet<int>? parcels))
            return false;

        peer.SceneListener = new SceneListenerState(request.Realm, parcels, cellMapper.ComputeCellKeys(parcels));
        return true;
    }

    protected override void OnAuthenticated(PeerIndex from, PeerState peer, ClientMessage message) =>
        PulseMetrics.SceneListener.CONNECTED.Add(1);

    protected override void LogAccepted(PeerIndex from, PeerState peer)
    {
        SceneListenerState listener = peer.SceneListener!;

        logger.LogInformation("Scene listener accepted with wallet {Wallet} - peerId {Peer} ({ParcelCount} parcels, realm '{Realm}')",
            peer.WalletId, from, listener.Parcels.Count, listener.Realm);
    }
}
