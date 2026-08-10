using Decentraland.Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;

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

        logger.LogDebug("Received input from {Peer} at {GlobalPosition}, anim state {AnimationFlags}",
            from.Value, snapshot.GlobalPosition, snapshot.AnimationFlags);
    }

    // Compare the raw quantized codes the client sent against the codes already stored — exact uint
    // equality, so a stopped peer re-sending the same codes suppresses the republish (and its seq bump)
    // regardless of any float rounding.
    private static bool IsSameState(in PeerSnapshot current, PlayerState incoming) =>
        current.Parcel == incoming.ParcelIndex
        && current.PositionX == incoming.PositionX
        && current.PositionY == incoming.PositionY
        && current.PositionZ == incoming.PositionZ
        && current.VelocityX == incoming.VelocityX
        && current.VelocityY == incoming.VelocityY
        && current.VelocityZ == incoming.VelocityZ
        && current.RotationY == incoming.RotationY
        && current.JumpCount == incoming.JumpCount
        && current.MovementBlend == incoming.MovementBlend
        && current.SlideBlend == incoming.SlideBlend
        && current.HeadYaw == (incoming.HasHeadYaw ? incoming.HeadYaw : null)
        && current.HeadPitch == (incoming.HasHeadPitch ? incoming.HeadPitch : null)
        && PointAtEquals(current.PointAt, incoming.GetPointAtRaw())
        && current.AnimationFlags == (PlayerAnimationFlags)incoming.StateFlags
        && current.GlideState == incoming.GlideState
        // Ensures that the first movement input after a teleport is always published,
        // even if the position/state values happen to be identical
        && !current.IsTeleport;

    private static bool PointAtEquals(QuantizedPointAt? a, (uint X, uint Y, uint Z)? b) =>
        (!a.HasValue && !b.HasValue)
        || (a.HasValue && b.HasValue
            && a.Value.X == b.Value.X
            && a.Value.Y == b.Value.Y
            && a.Value.Z == b.Value.Z);
}
