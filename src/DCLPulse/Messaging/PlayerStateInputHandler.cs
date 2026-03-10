using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class PlayerStateInputHandler(ITimeProvider timeProvider, SnapshotBoard snapshotBoard) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (!peers.TryGetValue(from, out PeerState? state) || state.ConnectionState != PeerConnectionState.AUTHENTICATED)

            // Skip messages from unauthenticated peer
            // TODO add analytics to understand if there is a problem
            return;

        PlayerStateInput input = message.Input;

        var snapshot = new PeerSnapshot(
            snapshotBoard.LastSeq(from) + 1,
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

        snapshotBoard.Publish(from, in snapshot);
    }
}
