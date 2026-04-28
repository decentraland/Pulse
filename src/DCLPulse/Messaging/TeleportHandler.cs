using Decentraland.Common;
using Decentraland.Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class TeleportHandler(ILogger<TeleportHandler> logger,
    SnapshotBoard snapshotBoard,
    PeerSnapshotPublisher snapshotPublisher,
    DiscreteEventRateLimiter rateLimiter,
    FieldValidator fieldValidator)
    : RuntimePacketHandlerBase<TeleportHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out PeerState? peerState))
            return;

        if (!rateLimiter.TryAccept(from, peerState))
            return;

        if (!fieldValidator.ValidateTeleport(from, peerState, message.Teleport))
            return;

        TeleportRequest request = message.Teleport;
        string realm = request.Realm;
        Vector3 localPosition = request.Position;
        int parcelIndex = request.ParcelIndex;

        // Read the prior realm before publishing so the realm-change log can compare against
        // the pre-teleport state. The publisher does its own snapshot read internally for
        // rotation/head-IK inheritance — minor double-read, single-writer per slot guarantees
        // they observe the same prior snapshot.
        string? previousRealm = snapshotBoard.TryRead(from, out PeerSnapshot prev) ? prev.Realm : null;

        PeerSnapshot snapshot = snapshotPublisher.PublishTeleport(from, parcelIndex, localPosition, realm);

        if (!string.Equals(previousRealm, realm, StringComparison.Ordinal))
        {
            logger.LogInformation("Peer {Peer} teleported to realm '{Realm}' (was '{Previous}') at {Position}",
                from, realm, previousRealm ?? "<none>", snapshot.GlobalPosition);
        }
        else
        {
            logger.LogInformation("Teleport requested by {Peer} at {Position} (realm '{Realm}')",
                from, snapshot.GlobalPosition, realm);
        }
    }
}
