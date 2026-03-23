using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class EmoteStartHandler(EmoteBoard emoteBoard, ITimeProvider timeProvider, ILogger<EmoteStartHandler> logger)
    : RuntimePacketHandlerBase<EmoteStartHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        EmoteStart emoteStart = message.EmoteStart;

        emoteBoard.Start(from, emoteStart.EmoteId, timeProvider.MonotonicTime);

        logger.LogDebug("Peer {Peer} started emote {EmoteId}", from.Value, emoteStart.EmoteId);
    }
}