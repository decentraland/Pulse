using Decentraland.Common;
using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class TeleportHandler(ILogger<TeleportHandler> logger,
    ITimeProvider timeProvider,
    SnapshotBoard snapshotBoard,
    SpatialGrid spatialGrid,
    ParcelEncoder parcelEncoder)
    : RuntimePacketHandlerBase<TeleportHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        Vector3 localPosition = message.Teleport.Position;
        int parcelIndex = message.Teleport.ParcelIndex;
        System.Numerics.Vector3 globalPosition = parcelEncoder.DecodeToGlobalPosition(parcelIndex, localPosition);

        float rotationY = 0;
        float? headYaw = null, headPitch = null;

        if (snapshotBoard.TryRead(from, out PeerSnapshot prevSnapshot))
        {
            rotationY = prevSnapshot.RotationY;
            headYaw = prevSnapshot.HeadYaw;
            headPitch = prevSnapshot.HeadPitch;
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
            IsTeleport: true);

        snapshotBoard.Publish(from, snapshot);
        spatialGrid.Set(from, snapshot.GlobalPosition);

        logger.LogInformation("Teleport requested by {Peer} at {Position}", from, globalPosition);
    }
}
