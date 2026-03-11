using System.Runtime.CompilerServices;
using Decentraland.Pulse;

namespace Pulse.Peers;

public static class PeerViewDiff
{
    private const float EPSILON = 0.001f;

    /// <summary>
    ///     Creates a diff message between two snapshots based on the given tier.
    /// </summary>
    public static PlayerStateDeltaTier0 CreateMessage(PeerIndex subjectId, PeerSnapshot from, PeerSnapshot to, PeerViewSimulationTier tier)
    {
        var delta = new PlayerStateDeltaTier0
        {
            SubjectId = subjectId,
            NewSeq = to.Seq,
            ServerTick = to.ServerTick,
            StateFlags = (uint)to.AnimationFlags,
        };

        // TIER_0: animation details and head IK
        if (tier.Equals(PeerViewSimulationTier.TIER_0))
        {
            if (!Equals(from.SlideBlend, to.SlideBlend))
                delta.SlideBlendQuantized = to.SlideBlend;

            if (!Equals(from.HeadYaw, to.HeadYaw) && to.HeadYaw.HasValue)
                delta.HeadYawQuantized = to.HeadYaw.Value;

            if (!Equals(from.HeadPitch, to.HeadPitch) && to.HeadPitch.HasValue)
                delta.HeadPitchQuantized = to.HeadPitch.Value;
        }

        // Glide State is important for every tier as it can be seen from afar
        if (from.GlideState != to.GlideState)
            delta.GlideState = to.GlideState;

        if (!Equals(from.Position.X, to.Position.X))
            delta.PositionXQuantized = to.Position.X;

        if (!Equals(from.Position.Y, to.Position.Y))
            delta.PositionYQuantized = to.Position.Y;

        if (!Equals(from.Position.Z, to.Position.Z))
            delta.PositionZQuantized = to.Position.Z;

        if (!Equals(from.RotationY, to.RotationY))
            delta.RotationYQuantized = to.RotationY;

        // TIER_2: spatial state flags only
        if (tier.Equals(PeerViewSimulationTier.TIER_0) || tier.Equals(PeerViewSimulationTier.TIER_1))
        {
            if (!Equals(from.Velocity.X, to.Velocity.X))
                delta.VelocityXQuantized = to.Velocity.X;

            if (!Equals(from.Velocity.Y, to.Velocity.Y))
                delta.VelocityYQuantized = to.Velocity.Y;

            if (!Equals(from.Velocity.Z, to.Velocity.Z))
                delta.VelocityZQuantized = to.Velocity.Z;

            if (!Equals(from.MovementBlend, to.MovementBlend))
                delta.MovementBlendQuantized = to.MovementBlend;
        }

        return delta;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Equals(float a, float b) =>
        Math.Abs(a - b) < EPSILON;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Equals(float? a, float? b)
    {
        if (a.HasValue != b.HasValue) return false;
        if (!a.HasValue) return true;
        return Equals(a.Value, b!.Value);
    }
}
