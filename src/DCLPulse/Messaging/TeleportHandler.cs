using Decentraland.Common;
using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class TeleportHandler(ILogger<TeleportHandler> logger,
    ITimeProvider timeProvider,
    SnapshotBoard snapshotBoard,
    SpatialGrid spatialGrid,
    ParcelEncoder parcelEncoder,
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
        System.Numerics.Vector3 globalPosition = parcelEncoder.DecodeToGlobalPosition(parcelIndex, localPosition);

        float rotationY = 0;
        float? headYaw = null, headPitch = null;
        string? previousRealm = null;

        if (snapshotBoard.TryRead(from, out PeerSnapshot prevSnapshot))
        {
            rotationY = prevSnapshot.RotationY;
            headYaw = prevSnapshot.HeadYaw;
            headPitch = prevSnapshot.HeadPitch;
            previousRealm = prevSnapshot.Realm;
        }

        var snapshot = new PeerSnapshot(snapshotBoard.LastSeq(from) + 1,
            timeProvider.MonotonicTime,
            parcelIndex, localPosition, globalPosition,
            System.Numerics.Vector3.Zero,
            rotationY,
            JumpCount: 0, MovementBlend: 0, SlideBlend: 0,
            headYaw, headPitch,
            PlayerAnimationFlags.Grounded,
            GlideState.PropClosed,
            IsTeleport: true,
            Realm: realm);

        snapshotBoard.Publish(from, snapshot);
        spatialGrid.Set(from, snapshot.GlobalPosition);

        if (!string.Equals(previousRealm, realm, StringComparison.Ordinal))
        {
            logger.LogInformation("Peer {Peer} teleported to realm '{Realm}' (was '{Previous}') at {Position}",
                from, realm, previousRealm ?? "<none>", globalPosition);
        }
        else
        {
            logger.LogInformation("Teleport requested by {Peer} at {Position} (realm '{Realm}')",
                from, globalPosition, realm);
        }
    }
}
