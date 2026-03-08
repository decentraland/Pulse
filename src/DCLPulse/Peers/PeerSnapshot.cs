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
    Vector3 Position,
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
