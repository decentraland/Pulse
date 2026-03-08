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
    }
}
