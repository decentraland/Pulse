using Decentraland.Pulse;
using static Pulse.Peers.DiffComparison;

namespace Pulse.Peers;

public static class PeerViewDiff
{
    /// <summary>
    ///     Creates a diff message between two snapshots based on the given tier.
    /// </summary>
    public static PlayerStateDeltaTier0 CreateMessage(PeerIndex subjectId, PeerSnapshot from, PeerSnapshot to, PeerViewSimulationTier tier)
    {
        var delta = new PlayerStateDeltaTier0
        {
            SubjectId = subjectId,
            BaselineSeq = from.Seq,
            NewSeq = to.Seq,
            ServerTick = to.ServerTick,
        };

        // TIER_0: animation details and head IK
        if (tier.Equals(PeerViewSimulationTier.TIER_0))
        {
            if (!FloatEquals(from.SlideBlend, to.SlideBlend))
            {
                delta.SlideBlendQuantized = to.SlideBlend;
            }

            if (!FloatEquals(from.HeadYaw, to.HeadYaw) && to.HeadYaw.HasValue)
            {
                delta.HeadYawQuantized = to.HeadYaw.Value;
            }

            if (!FloatEquals(from.HeadPitch, to.HeadPitch) && to.HeadPitch.HasValue)
            {
                delta.HeadPitchQuantized = to.HeadPitch.Value;
            }
        }

        if (from.AnimationFlags != to.AnimationFlags)
            delta.StateFlags = (uint)to.AnimationFlags;

        // Glide State is important for every tier as it can be seen from afar
        if (from.GlideState != to.GlideState)
            delta.GlideState = to.GlideState;

        if (!FloatEquals(from.Parcel, to.Parcel))
            delta.ParcelIndex = to.Parcel;

        if (!FloatEquals(from.LocalPosition.X, to.LocalPosition.X))
            delta.PositionXQuantized = to.LocalPosition.X;

        if (!FloatEquals(from.LocalPosition.Y, to.LocalPosition.Y))
            delta.PositionYQuantized = to.LocalPosition.Y;

        if (!FloatEquals(from.LocalPosition.Z, to.LocalPosition.Z))
            delta.PositionZQuantized = to.LocalPosition.Z;

        if (!FloatEquals(from.RotationY, to.RotationY))
            delta.RotationYQuantized = to.RotationY;

        if (!FloatEquals(from.JumpCount, to.JumpCount))
            delta.JumpCount = to.JumpCount;

        // TIER_2: spatial state flags only
        if (tier.Equals(PeerViewSimulationTier.TIER_0) || tier.Equals(PeerViewSimulationTier.TIER_1))
        {
            if (!FloatEquals(from.Velocity.X, to.Velocity.X))
                delta.VelocityXQuantized = to.Velocity.X;

            if (!FloatEquals(from.Velocity.Y, to.Velocity.Y))
                delta.VelocityYQuantized = to.Velocity.Y;

            if (!FloatEquals(from.Velocity.Z, to.Velocity.Z))
                delta.VelocityZQuantized = to.Velocity.Z;

            if (!FloatEquals(from.MovementBlend, to.MovementBlend))
                delta.MovementBlendQuantized = to.MovementBlend;
        }

        // Sequence increment is controlled by "PlayerStateInputHandler",
        // even if "nothing" has changed between 2 snapshots, the seq should be advanced on the client, otherwise it creates a gap and a RESYNC request
        return delta;
    }
}
