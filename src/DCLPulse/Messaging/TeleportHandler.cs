using Decentraland.Common;
using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class TeleportHandler(ILogger<TeleportHandler> logger,
    TeleportBoard teleportBoard, ITimeProvider timeProvider)
    : RuntimePacketHandlerBase<TeleportHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        // TODO: should we modify the peer's snapshot to the new position?
        Vector3 globalPosition = message.Teleport.Position;
        teleportBoard.Publish(from, globalPosition, timeProvider.MonotonicTime);

        logger.LogInformation("Teleport requested by {Peer} to {Position}", from, globalPosition);
    }
}
