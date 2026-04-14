using Decentraland.Pulse;
using System.Numerics;

namespace Pulse.Peers;

/// <summary>
///     Emote metadata carried on a snapshot. Null on PeerSnapshot means no emote activity.
/// </summary>
public record struct EmoteState(
    string? EmoteId,
    uint StartTick,
    uint? DurationMs = null,
    EmoteStopReason? StopReason = null
);

public static class PeerSnapshotExtensions
{
    public static bool IsEmoting(this in PeerSnapshot snapshot) =>
        snapshot.Emote?.EmoteId != null;
}

/// <summary>
///     Contains positional and animation state for at a given moment of time
/// </summary>
public record struct PeerSnapshot(

    // Server-related
    uint Seq,
    uint ServerTick,

    // Positional
    int Parcel,
    Vector3 LocalPosition,
    Vector3 GlobalPosition,
    Vector3 Velocity,
    float RotationY,

    // Animation-related
    int JumpCount,
    float MovementBlend,
    float SlideBlend,
    float? HeadYaw,
    float? HeadPitch,
    PlayerAnimationFlags AnimationFlags,
    GlideState GlideState,

    // Flags
    bool IsTeleport = false,

    // Emote — non-null means emote start or stop activity on this snapshot
    EmoteState? Emote = null
);
