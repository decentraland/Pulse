using Decentraland.Pulse;
using System.Numerics;

namespace Pulse.Peers;

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
    float MovementBlend,
    float SlideBlend,
    float? HeadYaw,
    float? HeadPitch,
    PlayerAnimationFlags AnimationFlags,
    GlideState GlideState
);
