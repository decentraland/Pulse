using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;

namespace Pulse.Messaging;

public class EmoteStartHandler(
    EmoteBoard emoteBoard,
    SnapshotBoard snapshotBoard,
    SpatialGrid spatialGrid,
    ITimeProvider timeProvider,
    ILogger<EmoteStartHandler> logger,
    ParcelEncoder parcelEncoder)
    : RuntimePacketHandlerBase<EmoteStartHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        EmoteStart emoteStart = message.EmoteStart;

        uint? durationMs = emoteStart.HasDurationMs ? emoteStart.DurationMs : null;
        uint now = timeProvider.MonotonicTime;

        emoteBoard.Start(from, emoteStart.EmoteId, now, durationMs);

        PlayerState state = emoteStart.PlayerState;
        Vector3 globalPosition = parcelEncoder.DecodeToGlobalPosition(state.ParcelIndex, state.Position);

        var snapshot = new PeerSnapshot(
            snapshotBoard.LastSeq(from) + 1,
            now,
            state.ParcelIndex,
            state.Position,
            globalPosition,
            state.Velocity,
            state.RotationY,
            state.JumpCount,
            state.MovementBlend,
            state.SlideBlend,
            state.GetHeadYaw(),
            state.GetHeadPitch(),
            (PlayerAnimationFlags)state.StateFlags,
            state.GlideState);

        snapshotBoard.Publish(from, in snapshot);
        spatialGrid.Set(from, snapshot.GlobalPosition);

        logger.LogInformation("Peer {Peer} started emote {EmoteId}", from.Value, emoteStart.EmoteId);
    }
}
