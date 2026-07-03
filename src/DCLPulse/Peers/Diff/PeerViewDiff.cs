using Decentraland.Pulse;

namespace Pulse.Peers;

public static class PeerViewDiff
{
    /// <summary>
    ///     Creates a diff message between two snapshots based on the given tier. Snapshots hold the
    ///     raw quantized wire codes, so fields are compared and copied as exact uints — any one-code
    ///     change is a diff, and the code goes onto the wire without a decode/re-encode step.
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
            if (from.SlideBlend != to.SlideBlend)
                delta.SlideBlend = to.SlideBlend;

            if (from.HeadYaw != to.HeadYaw && to.HeadYaw.HasValue)
                delta.HeadYaw = to.HeadYaw.Value;

            if (from.HeadPitch != to.HeadPitch && to.HeadPitch.HasValue)
                delta.HeadPitch = to.HeadPitch.Value;
        }

        if (from.AnimationFlags != to.AnimationFlags)
            delta.StateFlags = (uint)to.AnimationFlags;

        // Glide State is important for every tier as it can be seen from afar
        if (from.GlideState != to.GlideState)
            delta.GlideState = to.GlideState;

        if (from.Parcel != to.Parcel)
            delta.ParcelIndex = to.Parcel;

        if (from.PositionX != to.PositionX)
            delta.PositionX = to.PositionX;

        if (from.PositionY != to.PositionY)
            delta.PositionY = to.PositionY;

        if (from.PositionZ != to.PositionZ)
            delta.PositionZ = to.PositionZ;

        if (from.RotationY != to.RotationY)
            delta.RotationY = to.RotationY;

        if (from.JumpCount != to.JumpCount)
            delta.JumpCount = to.JumpCount;

        if (to.PointAt.HasValue)
        {
            QuantizedPointAt toPointAt = to.PointAt.Value;

            if (!from.PointAt.HasValue || from.PointAt.Value.X != toPointAt.X)
                delta.PointAtX = toPointAt.X;

            if (!from.PointAt.HasValue || from.PointAt.Value.Y != toPointAt.Y)
                delta.PointAtY = toPointAt.Y;

            if (!from.PointAt.HasValue || from.PointAt.Value.Z != toPointAt.Z)
                delta.PointAtZ = toPointAt.Z;
        }

        // TIER_2: spatial state flags only
        if (tier.Equals(PeerViewSimulationTier.TIER_0) || tier.Equals(PeerViewSimulationTier.TIER_1))
        {
            if (from.VelocityX != to.VelocityX)
                delta.VelocityX = to.VelocityX;

            if (from.VelocityY != to.VelocityY)
                delta.VelocityY = to.VelocityY;

            if (from.VelocityZ != to.VelocityZ)
                delta.VelocityZ = to.VelocityZ;

            if (from.MovementBlend != to.MovementBlend)
                delta.MovementBlend = to.MovementBlend;
        }

        // Sequence increment is controlled by "PlayerStateInputHandler",
        // even if "nothing" has changed between 2 snapshots, the seq should be advanced on the client, otherwise it creates a gap and a RESYNC request
        return delta;
    }
}
