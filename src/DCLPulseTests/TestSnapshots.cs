using Decentraland.Pulse;
using Pulse.Peers;
using System.Numerics;

namespace DCLPulseTests;

/// <summary>
///     Test helpers for the raw-quantized <see cref="PeerSnapshot" />. <see cref="Make" /> takes the
///     human-readable float values a test cares about and encodes them into the wire codes the snapshot
///     stores; the <c>Decode*</c> extensions go the other way for assertions. Both round-trip through the
///     generated <c>*Quantized</c> accessors so the quantization params stay in lockstep with the proto.
/// </summary>
internal static class TestSnapshots
{
    public static PeerSnapshot Make(
        uint seq = 1,
        uint serverTick = 0,
        int parcel = 0,
        Vector3 position = default,
        Vector3 velocity = default,
        float rotationY = 0f,
        int jumpCount = 0,
        float movementBlend = 0f,
        float slideBlend = 0f,
        float? headYaw = null,
        float? headPitch = null,
        Vector3? pointAt = null,
        PlayerAnimationFlags animationFlags = PlayerAnimationFlags.None,
        GlideState glideState = GlideState.PropClosed,
        bool isTeleport = false,
        EmoteState? emote = null,
        string? realm = null,
        Vector3? globalPosition = null)
    {
        var e = new PlayerState
        {
            PositionXQuantized = position.X,
            PositionYQuantized = position.Y,
            PositionZQuantized = position.Z,
            VelocityXQuantized = velocity.X,
            VelocityYQuantized = velocity.Y,
            VelocityZQuantized = velocity.Z,
            RotationYQuantized = rotationY,
            MovementBlendQuantized = movementBlend,
            SlideBlendQuantized = slideBlend,
        };

        uint? headYawCode = null, headPitchCode = null;

        if (headYaw.HasValue)
        {
            e.HeadYawQuantized = headYaw.Value;
            headYawCode = e.HeadYaw;
        }

        if (headPitch.HasValue)
        {
            e.HeadPitchQuantized = headPitch.Value;
            headPitchCode = e.HeadPitch;
        }

        QuantizedPointAt? pointAtCodes = null;

        if (pointAt is { } p)
        {
            e.PointAtXQuantized = p.X;
            e.PointAtYQuantized = p.Y;
            e.PointAtZQuantized = p.Z;
            pointAtCodes = new QuantizedPointAt(e.PointAtX, e.PointAtY, e.PointAtZ);
        }

        return new PeerSnapshot(
            Seq: seq,
            ServerTick: serverTick,
            Parcel: parcel,
            PositionX: e.PositionX,
            PositionY: e.PositionY,
            PositionZ: e.PositionZ,
            VelocityX: e.VelocityX,
            VelocityY: e.VelocityY,
            VelocityZ: e.VelocityZ,
            GlobalPosition: globalPosition ?? position,
            RotationY: e.RotationY,
            JumpCount: jumpCount,
            MovementBlend: e.MovementBlend,
            SlideBlend: e.SlideBlend,
            HeadYaw: headYawCode,
            HeadPitch: headPitchCode,
            PointAt: pointAtCodes,
            AnimationFlags: animationFlags,
            GlideState: glideState,
            IsTeleport: isTeleport,
            Emote: emote,
            Realm: realm);
    }

    public static Vector3 DecodePosition(this in PeerSnapshot s)
    {
        var p = new PlayerState { PositionX = s.PositionX, PositionY = s.PositionY, PositionZ = s.PositionZ };
        return new Vector3(p.PositionXQuantized, p.PositionYQuantized, p.PositionZQuantized);
    }

    public static Vector3 DecodeVelocity(this in PeerSnapshot s)
    {
        var p = new PlayerState { VelocityX = s.VelocityX, VelocityY = s.VelocityY, VelocityZ = s.VelocityZ };
        return new Vector3(p.VelocityXQuantized, p.VelocityYQuantized, p.VelocityZQuantized);
    }

    public static float DecodeRotationY(this in PeerSnapshot s) =>
        new PlayerState { RotationY = s.RotationY }.RotationYQuantized;

    public static float DecodeMovementBlend(this in PeerSnapshot s) =>
        new PlayerState { MovementBlend = s.MovementBlend }.MovementBlendQuantized;

    public static float DecodeSlideBlend(this in PeerSnapshot s) =>
        new PlayerState { SlideBlend = s.SlideBlend }.SlideBlendQuantized;

    public static float? DecodeHeadYaw(this in PeerSnapshot s) =>
        s.HeadYaw is { } code ? new PlayerState { HeadYaw = code }.HeadYawQuantized : null;

    public static float? DecodeHeadPitch(this in PeerSnapshot s) =>
        s.HeadPitch is { } code ? new PlayerState { HeadPitch = code }.HeadPitchQuantized : null;

    public static Vector3? DecodePointAt(this in PeerSnapshot s)
    {
        if (s.PointAt is not { } pa)
            return null;

        var p = new PlayerState { PointAtX = pa.X, PointAtY = pa.Y, PointAtZ = pa.Z };
        return new Vector3(p.PointAtXQuantized, p.PointAtYQuantized, p.PointAtZQuantized);
    }
}
