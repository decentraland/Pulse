using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class EmoteStopHandler(EmoteBoard emoteBoard, ILogger<EmoteStopHandler> logger)
    : RuntimePacketHandlerBase<EmoteStopHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        if (emoteBoard.GetCurrentEmote(from) == null)
        {
            logger.LogWarning("Peer {Peer} sent EmoteStop but no emote is active", from.Value);
            return;
        }

        logger.LogDebug("Peer {Peer} stopped emote", from.Value);

        emoteBoard.Stop(from);
    }
}