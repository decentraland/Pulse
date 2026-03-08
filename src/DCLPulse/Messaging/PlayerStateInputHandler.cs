using Decentraland.Pulse;
using Pulse.Peers;

namespace Pulse.Messaging;

public class PlayerStateInputHandler(ITimeProvider timeProvider)
{
    public void Handle(PeerState peerState, PlayerStateInput input)
    {
        var snapshot = new PeerSnapshot(
            peerState.SnapshotHistory.LastSeq + 1,
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

        peerState.SnapshotHistory.Store(snapshot);
    }
}
