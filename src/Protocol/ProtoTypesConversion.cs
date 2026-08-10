namespace Decentraland.Common
{
    public partial class Vector3
    {
        public static implicit operator System.Numerics.Vector3(Vector3 v) =>
            new (v.X, v.Y, v.Z);
    }
}

namespace Decentraland.Pulse
{
    public partial class PlayerState
    {
        // Decode the quantized position into a world vector at the read boundary. Used only to derive
        // the snapshot's GlobalPosition (interest management); the raw codes are stored/relayed as-is.
        public System.Numerics.Vector3 GetPosition() =>
            new (PositionXQuantized, PositionYQuantized, PositionZQuantized);

        // point_at is "only meaningful when POINTING_AT is set in state_flags" per the proto. Return the
        // raw quantized codes only when the flag is set AND all three components are present; a partial
        // triple from a misbehaving client would otherwise yield min-valued Y/Z.
        public (uint X, uint Y, uint Z)? GetPointAtRaw() =>
            (StateFlags & (uint)PlayerAnimationFlags.PointingAt) != 0 && HasPointAtX && HasPointAtY && HasPointAtZ
                ? (PointAtX, PointAtY, PointAtZ)
                : null;
    }
}
