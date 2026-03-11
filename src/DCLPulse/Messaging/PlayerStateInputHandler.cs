using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class PlayerStateInputHandler(
    ITimeProvider timeProvider,
    SnapshotBoard snapshotBoard,
    SpatialGrid spatialGrid,
    ILogger<PlayerStateInputHandler> logger)
    : RuntimePacketHandlerBase<PlayerStateInputHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        PlayerStateInput input = message.Input;

        var snapshot = new PeerSnapshot(
            snapshotBoard.LastSeq(from) + 1,
            timeProvider.MonotonicTime,
            input.State.ParcelIndex,
            input.State.Position,
            input.State.Velocity,
            input.State.RotationY,
            input.State.MovementBlend,
            input.State.SlideBlend,
            input.State.GetHeadYaw(),
            input.State.GetHeadPitch(),
            (PlayerAnimationFlags)input.State.StateFlags,
            input.State.GlideState);

        snapshotBoard.Publish(from, in snapshot);
        spatialGrid.Set(from, snapshot.Position);
    }
}
