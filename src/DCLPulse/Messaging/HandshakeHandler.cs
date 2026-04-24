using DCL.Auth;
using Decentraland.Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;
using System.Text.Json;

namespace Pulse.Messaging;

public class HandshakeHandler(MessagePipe messagePipe,
    AuthChainValidator authChainValidator,
    PeerStateFactory peerStateFactory,
    SnapshotBoard snapshotBoard,
    IdentityBoard identityBoard,
    ITransport transport,
    HandshakeAttemptPolicy attemptPolicy,
    PreAuthAdmission preAuthAdmission,
    HandshakeReplayPolicy replayPolicy,
    BanList banList,
    ILogger<HandshakeHandler> logger) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        peers.TryGetValue(from, out PeerState? existingState);

        // Throttle before any parsing/crypto work — attempt counter is per-peer on PeerState.
        // Policy owns the disconnect on violation.
        if (existingState != null && !attemptPolicy.TryRecordAttempt(from, existingState))
            return;

        HandshakeRequest handshakeRequest = message.Handshake;
        string authChainJson = handshakeRequest.AuthChain.ToStringUtf8();
        Dictionary<string, string>? headers = JsonSerializer.Deserialize(authChainJson, HandshakeJsonContext.Default.DictionaryStringString);

        if (headers == null)
        {
            messagePipe.Send(new MessagePipe.OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = false,
                    Error = "Invalid auth chain JSON",
                },
            }, PacketMode.RELIABLE));

            logger.LogInformation("Handshake validation failed: cannot parse auth-chain");

            return;
        }

        try
        {
            IReadOnlyList<AuthLink> chain = AuthChainParser.ParseFromSignedFetchHeaders(headers);

            string timestamp = string.Empty;
            string metadata = string.Empty;

            foreach (KeyValuePair<string, string> kv in headers)
            {
                if (kv.Key.Equals("x-identity-timestamp", StringComparison.OrdinalIgnoreCase))
                    timestamp = kv.Value;

                if (kv.Key.Equals("x-identity-metadata", StringComparison.OrdinalIgnoreCase))
                    metadata = kv.Value;
            }

            string expectedPayload = SignedFetch.BuildSignedFetchPayload("connect", "/", timestamp, metadata);
            AuthChainValidationResult result = authChainValidator.Validate(chain, expectedPayload);

            // Platform ban list — checked before the replay cache so a banned wallet doesn't
            // consume an anti-replay slot. The ban list is populated by BansPollingHttpService on a
            // background timer; when the poller is disabled (no moderator token) the list is
            // empty and this check is a constant-time no-op.
            if (existingState != null && banList.IsBanned(result.UserAddress))
            {
                RejectBanned(from, existingState, result.UserAddress);
                return;
            }

            // Replay guard — a valid signature alone isn't enough. Reject handshakes whose
            // (wallet, timestamp) pair has already been accepted within the anti-replay window.
            // The cache owns the disconnect; state flips to PENDING_DISCONNECT via PeerDefense.
            if (existingState != null && !replayPolicy.TryAdmit(from, existingState, result.UserAddress, timestamp))
                return;

            PeerState peer = peerStateFactory.Create();
            peer.WalletId = result.UserAddress;
            peer.ConnectionState = PeerConnectionState.AUTHENTICATED;

            peers[from] = peer;

            // Promotion out of PENDING_AUTH — frees both the global pre-auth budget and the
            // per-IP pre-auth slot in one call.
            preAuthAdmission.ReleaseOnPromotion(from);

            if (identityBoard.TryGetPeerIndexByWallet(peer.WalletId, out PeerIndex duplicatedPeer))
            {
                if (duplicatedPeer != from)
                {
                    transport.Disconnect(duplicatedPeer, DisconnectReason.DUPLICATE_SESSION);
                    logger.LogInformation("Duplicated peer found {Wallet}, disconnecting peer {Peer}", result.UserAddress, duplicatedPeer);
                }
            }

            identityBoard.Set(from, result.UserAddress);
            snapshotBoard.SetActive(from);

            messagePipe.Send(new MessagePipe.OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = true,
                },
            }, PacketMode.RELIABLE));

            logger.LogInformation("Peer handshake accepted with wallet {Wallet} - peerId {Peer}", result.UserAddress, from);
        }
        catch (Exception e)
        {
            messagePipe.Send(new MessagePipe.OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = false,
                    Error = e.Message,
                },
            }, PacketMode.RELIABLE));

            logger.LogInformation("Handshake validation failed: {Error}", e.Message);
        }
    }

    private void RejectBanned(PeerIndex from, PeerState state, string wallet)
    {
        messagePipe.Send(new MessagePipe.OutgoingMessage(from, new ServerMessage
        {
            Handshake = new HandshakeResponse
            {
                Success = false,
                Error = "banned",
            },
        }, PacketMode.RELIABLE));

        state.ConnectionState = PeerConnectionState.PENDING_DISCONNECT;
        PulseMetrics.Hardening.BANNED_REFUSED.Add(1);
        transport.Disconnect(from, DisconnectReason.BANNED);

        logger.LogInformation("Handshake rejected: wallet {Wallet} is banned", wallet);
    }
}
