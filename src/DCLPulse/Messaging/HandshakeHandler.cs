using DCL.Auth;
using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Text.Json;

namespace Pulse.Messaging;

public class HandshakeHandler(MessagePipe messagePipe,
    AuthChainValidator authChainValidator,
    PeerStateFactory peerStateFactory,
    SnapshotBoard snapshotBoard,
    IdentityBoard identityBoard,
    ITransport transport,
    ILogger<HandshakeHandler> logger) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
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

            PeerState peer = peerStateFactory.Create();
            peer.WalletId = result.UserAddress;
            peer.ConnectionState = PeerConnectionState.AUTHENTICATED;

            peers[from] = peer;

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
}
