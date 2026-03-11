using Decentraland.Pulse;
using Pulse.Peers;

namespace Pulse.Messaging;

public class ResyncRequestHandler(ILogger<ResyncRequestHandler> logger) : RuntimePacketHandlerBase<ResyncRequestHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out PeerState? state)) return;

        state.ResyncRequests ??= new Dictionary<PeerIndex, uint>();
        state.ResyncRequests[new PeerIndex(message.Resync.SubjectId)] = message.Resync.KnownSeq;
    }
}
