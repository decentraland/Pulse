using Decentraland.Pulse;
using Pulse.Peers;

namespace Pulse.Messaging;

public class ResyncRequestHandler(ILogger<ResyncRequestHandler> logger) : RuntimePacketHandlerBase<ResyncRequestHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out PeerState? state)) return;

        // AoI enforcement is the simulation's job: `PeerSimulation.ProcessVisibleSubjects`
        // only consumes resync entries for subjects in the observer's current visible set
        // (the AoI collector). Requests for non-visible subjects are dropped at end-of-tick
        // by `ResyncRequests.Clear()`, so no STATE_FULL ever leaks for them. Keep that
        // invariant when touching the simulation — the handler deliberately does no lookup.
        state.ResyncRequests ??= new Dictionary<PeerIndex, uint>();
        state.ResyncRequests[new PeerIndex(message.Resync.SubjectId)] = message.Resync.KnownSeq;

        logger.LogInformation("Received resync request from {Peer} for subject {SubjectId} with known sequence {KnownSeq}",
            from.Value, message.Resync.SubjectId, message.Resync.KnownSeq);
    }
}
