using Decentraland.Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;
using static Pulse.Peers.DiffComparison;

namespace Pulse.Messaging;

public class PlayerStateInputHandler(
    SnapshotBoard snapshotBoard,
    PeerSnapshotPublisher snapshotPublisher,
    ILogger<PlayerStateInputHandler> logger,
    MovementInputRateLimiter rateLimiter,
    FieldValidator fieldValidator)
    : RuntimePacketHandlerBase<PlayerStateInputHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out PeerState? peerState))
            return;

        if (!rateLimiter.TryAccept(from, peerState))
            return;

        if (!fieldValidator.ValidatePlayerStateInput(from, peerState, message.Input))
            return;

        PlayerStateInput input = message.Input;
        PlayerState state = input.State;

        // Skip publishing if the state hasn't changed. Without this, seq increments on every
        // input message even when the peer is idle, causing observers to receive deltas with
        // non-contiguous sequences (gaps from suppressed no-diff deltas) and triggering more resync loops.
        if (snapshotBoard.TryRead(from, out PeerSnapshot current) && IsSameState(current, state))
            return;

        PeerSnapshot snapshot = snapshotPublisher.PublishFromPlayerState(from, state);

        logger.LogDebug("Received input from {Peer} with position {GlobalPosition}, rotation {RotationY}, velocity {Velocity}, movement blend {MovementBlend}, anim state {AnimationFlags}",
            from.Value, snapshot.GlobalPosition, snapshot.RotationY, snapshot.Velocity, snapshot.MovementBlend, snapshot.AnimationFlags);
    }

    private static bool IsSameState(in PeerSnapshot current, PlayerState incoming) =>
        current.Parcel == incoming.ParcelIndex
        && current.LocalPosition == (Vector3)incoming.Position
        && current.Velocity == (Vector3)incoming.Velocity
        && FloatEquals(current.RotationY, incoming.RotationY)
        && FloatEquals(current.MovementBlend, incoming.MovementBlend)
        && FloatEquals(current.SlideBlend, incoming.SlideBlend)
        && FloatEquals(current.HeadYaw, incoming.GetHeadYaw(), 0.5f)
        && FloatEquals(current.HeadPitch, incoming.GetHeadPitch(), 0.5f)
        && current.AnimationFlags == (PlayerAnimationFlags)incoming.StateFlags
        && current.GlideState == incoming.GlideState
        // Ensures that the first movement input after a teleport is always published,
        // even if the position/state values happen to be identical
        && !current.IsTeleport;
}
