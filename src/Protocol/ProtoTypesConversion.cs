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
        public float? GetHeadYaw() =>
            HasHeadYaw ? HeadYaw : null;

        public float? GetHeadPitch() =>
            HasHeadPitch ? HeadPitch : null;

        // point_at is "only meaningful when POINTING_AT is set in state_flags" per the proto —
        // gate at the read boundary so the rest of the server can treat the flag and the vector
        // as a single signal.
        public System.Numerics.Vector3? GetPointAt() =>
            (StateFlags & (uint)PlayerAnimationFlags.PointingAt) != 0 && PointAt != null
                ? (System.Numerics.Vector3)PointAt
                : null;
    }
}
