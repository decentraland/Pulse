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
    ITransport transport) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        HandshakeRequest handshakeRequest = message.Handshake;
        string authChainJson = handshakeRequest.AuthChain.ToStringUtf8();
        Dictionary<string, string>? headers = JsonSerializer.Deserialize<Dictionary<string, string>>(authChainJson);

        if (headers == null)
        {
            messagePipe.Send(new MessagePipe.OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = false,
                    Error = "Invalid auth chain JSON",
                },
            }, ITransport.PacketMode.RELIABLE));

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
            snapshotBoard.SetActive(from);

            messagePipe.Send(new MessagePipe.OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = true,
                },
            }, ITransport.PacketMode.RELIABLE));

            DisconnectDuplicatedSessions(peers, from, result);
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
            }, ITransport.PacketMode.RELIABLE));
        }
    }

    private void DisconnectDuplicatedSessions(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, AuthChainValidationResult result)
    {
        // TODO: could be improved if we index by walletId, although we would need to keep in sync both lists
        foreach ((PeerIndex pi, PeerState ps) in peers)
        {
            if (!string.Equals(ps.WalletId, result.UserAddress, StringComparison.OrdinalIgnoreCase)) continue;

            if (pi != from)
                transport.Disconnect(pi, ITransport.DisconnectReason.DuplicateSession);
        }
    }
}
