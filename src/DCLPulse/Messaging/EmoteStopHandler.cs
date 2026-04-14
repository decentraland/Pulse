using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class EmoteStopHandler(SnapshotBoard snapshotBoard, ITimeProvider timeProvider, ILogger<EmoteStopHandler> logger)
    : RuntimePacketHandlerBase<EmoteStopHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        if (!snapshotBoard.TryRead(from, out PeerSnapshot current) || !current.IsEmoting())
        {
            logger.LogWarning("Peer {Peer} sent EmoteStop but no emote is active", from.Value);
            return;
        }

        uint now = timeProvider.MonotonicTime;

        PeerSnapshot stop = current with
        {
            Seq = snapshotBoard.LastSeq(from) + 1,
            ServerTick = now,
            Emote = new EmoteState(null, current.Emote!.Value.StartTick,
                StopReason: EmoteStopReason.Cancelled),
        };

        snapshotBoard.Publish(from, in stop);

        logger.LogInformation("Peer {Peer} stopped emote", from.Value);
    }
}
