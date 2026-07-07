using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;

namespace Pulse.Messaging;

/// <summary>
///     Authenticates a scene-listener connection: same ECDSA auth chain and anti-abuse
///     pipeline as <see cref="HandshakeHandler" /> (attempt throttle, ban list, replay
///     guard), but the peer announces an immutable parcel-set AoI instead of an initial
///     state. The listener is never registered as a subject — no SnapshotBoard slot, no
///     SpatialGrid entry — so it stays invisible to every player observer. Re-announcing
///     the parcel set requires reconnecting: no post-auth message can mutate it.
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
    ILogger<SceneListenerHandshakeHandler> logger) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        // The router guarantees a Connected lifecycle event precedes the first message,
        // so a missing state means the peer is already being torn down — nothing to do.
        if (!peers.TryGetValue(from, out PeerState? existingState))
            return;

        if (!attemptPolicy.TryRecordAttempt(from, existingState))
            return;

        SceneListenerHandshakeRequest request = message.SceneListenerHandshake;

        try
        {
            HandshakeAuthenticator.AuthResult? auth = authenticator.Authenticate(request.AuthChain);

            if (auth == null)
            {
                SendResponse(from, success: false, "Invalid auth chain JSON");
                logger.LogInformation("Scene-listener handshake failed: cannot parse auth-chain");
                return;
            }

            (string wallet, string timestamp) = auth.Value;

            if (banList.IsBanned(wallet))
            {
                SendResponse(from, success: false, "banned");
                existingState.ConnectionState = PeerConnectionState.PENDING_DISCONNECT;
                PulseMetrics.Hardening.BANNED_REFUSED.Add(1);
                transport.Disconnect(from, DisconnectReason.BANNED);
                logger.LogInformation("Scene-listener handshake rejected: wallet {Wallet} is banned", wallet);
                return;
            }

            if (!replayPolicy.TryAdmit(from, existingState, wallet, timestamp))
                return;

            if (!fieldValidator.ValidateSceneListenerHandshake(from, existingState, request, out HashSet<int>? parcels))
                return;

            PeerState peer = peerStateFactory.Create();
            peer.WalletId = wallet;
            peer.ConnectionState = PeerConnectionState.AUTHENTICATED;
            peer.SceneListener = new SceneListenerState(request.Realm, parcels, cellMapper.ComputeCellKeys(parcels));

            peers[from] = peer;

            preAuthAdmission.ReleaseOnPromotion(from);

            if (identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex duplicatedPeer) && duplicatedPeer != from)
            {
                transport.Disconnect(duplicatedPeer, DisconnectReason.DUPLICATE_SESSION);
                logger.LogInformation("Duplicated peer found {Wallet}, disconnecting peer {Peer}", wallet, duplicatedPeer);
            }

            identityBoard.Set(from, wallet);

            // Deliberately no snapshotBoard.SetActive / snapshot seed / spatialGrid.Set:
            // a listener is never a subject.

            PulseMetrics.SceneListener.CONNECTED.Add(1);

            SendResponse(from, success: true, error: null);

            logger.LogInformation("Scene listener accepted with wallet {Wallet} - peerId {Peer} ({ParcelCount} parcels, realm '{Realm}')",
                wallet, from, parcels.Count, request.Realm);
        }
        catch (Exception e)
        {
            SendResponse(from, success: false, e.Message);
            logger.LogInformation("Scene-listener handshake failed: {Error}", e.Message);
        }
    }

    private void SendResponse(PeerIndex to, bool success, string? error)
    {
        var response = new HandshakeResponse { Success = success };

        if (error != null)
            response.Error = error;

        messagePipe.Send(new MessagePipe.OutgoingMessage(to, new ServerMessage
        {
            Handshake = response,
        }, PacketMode.RELIABLE));
    }
}
