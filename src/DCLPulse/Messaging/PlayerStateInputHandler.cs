using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class PlayerStateInputHandler(ITimeProvider timeProvider, SnapshotBoard snapshotBoard)
{
    public void Handle(PeerIndex peerIndex, PlayerStateInput input)
    {
        var snapshot = new PeerSnapshot(
            snapshotBoard.LastSeq(peerIndex) + 1,
            timeProvider.MonotonicTime,
            input.State.Position,
            input.State.Velocity,
            input.State.RotationY,
            input.State.MovementBlend,
            input.State.SlideBlend,
            input.State.GetHeadYaw(),
            input.State.GetHeadPitch(),
            (PlayerAnimationFlags)input.State.StateFlags,
            input.State.GlideState);

        snapshotBoard.Publish(peerIndex, in snapshot);
    }
}
