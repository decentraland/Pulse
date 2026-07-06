using Decentraland.Pulse;
using Google.Protobuf;

namespace DCLPulseTests;

[TestFixture]
public class PlayerStateQuantizationTests
{
    /// <summary>
    ///     PlayerState must quantize every shared field with the exact same options as
    ///     PlayerStateDeltaTier0, so a full state and a stream of deltas land on the same grid.
    ///     The two messages declare those options independently in the proto, so this locks them
    ///     against drift: serialize both through the wire (which discards the setter's cached float
    ///     and forces a decode of the stored uint32) and assert the decoded values are identical.
    ///     Inputs are deliberately off-grid so a bits/min/max mismatch surfaces as a difference.
    /// </summary>
    [Test]
    public void PlayerState_QuantizesIdenticallyTo_PlayerStateDeltaTier0()
    {
        const float posX = 5.3f, posY = 73.1f, posZ = 11.9f;
        const float velX = -12.4f, velY = 3.2f, velZ = 47.6f;
        const float rotationY = 217.4f, movementBlend = 1.7f, slideBlend = 0.35f;
        const float headYaw = 190.2f, headPitch = 44.9f;
        const float pointAtX = -812.5f, pointAtY = 133.3f, pointAtZ = 640.1f;

        var state = new PlayerState
        {
            PositionXQuantized = posX, PositionYQuantized = posY, PositionZQuantized = posZ,
            VelocityXQuantized = velX, VelocityYQuantized = velY, VelocityZQuantized = velZ,
            RotationYQuantized = rotationY, MovementBlendQuantized = movementBlend, SlideBlendQuantized = slideBlend,
            HeadYawQuantized = headYaw, HeadPitchQuantized = headPitch,
            PointAtXQuantized = pointAtX, PointAtYQuantized = pointAtY, PointAtZQuantized = pointAtZ,
        };

        var delta = new PlayerStateDeltaTier0
        {
            PositionXQuantized = posX, PositionYQuantized = posY, PositionZQuantized = posZ,
            VelocityXQuantized = velX, VelocityYQuantized = velY, VelocityZQuantized = velZ,
            RotationYQuantized = rotationY, MovementBlendQuantized = movementBlend, SlideBlendQuantized = slideBlend,
            HeadYawQuantized = headYaw, HeadPitchQuantized = headPitch,
            PointAtXQuantized = pointAtX, PointAtYQuantized = pointAtY, PointAtZQuantized = pointAtZ,
        };

        PlayerState s = PlayerState.Parser.ParseFrom(state.ToByteArray());
        PlayerStateDeltaTier0 d = PlayerStateDeltaTier0.Parser.ParseFrom(delta.ToByteArray());

        Assert.Multiple(() =>
        {
            Assert.That(s.PositionXQuantized, Is.EqualTo(d.PositionXQuantized), "position_x");
            Assert.That(s.PositionYQuantized, Is.EqualTo(d.PositionYQuantized), "position_y");
            Assert.That(s.PositionZQuantized, Is.EqualTo(d.PositionZQuantized), "position_z");
            Assert.That(s.VelocityXQuantized, Is.EqualTo(d.VelocityXQuantized), "velocity_x");
            Assert.That(s.VelocityYQuantized, Is.EqualTo(d.VelocityYQuantized), "velocity_y");
            Assert.That(s.VelocityZQuantized, Is.EqualTo(d.VelocityZQuantized), "velocity_z");
            Assert.That(s.RotationYQuantized, Is.EqualTo(d.RotationYQuantized), "rotation_y");
            Assert.That(s.MovementBlendQuantized, Is.EqualTo(d.MovementBlendQuantized), "movement_blend");
            Assert.That(s.SlideBlendQuantized, Is.EqualTo(d.SlideBlendQuantized), "slide_blend");
            Assert.That(s.HeadYawQuantized, Is.EqualTo(d.HeadYawQuantized), "head_yaw");
            Assert.That(s.HeadPitchQuantized, Is.EqualTo(d.HeadPitchQuantized), "head_pitch");
            Assert.That(s.PointAtXQuantized, Is.EqualTo(d.PointAtXQuantized), "point_at_x");
            Assert.That(s.PointAtYQuantized, Is.EqualTo(d.PointAtYQuantized), "point_at_y");
            Assert.That(s.PointAtZQuantized, Is.EqualTo(d.PointAtZQuantized), "point_at_z");
        });
    }

    /// <summary>
    ///     TeleportRequest carries a parcel-local position on the same grid as PlayerState, so its
    ///     position_x/y/z must quantize identically — otherwise a teleport and the movement that
    ///     follows it would place the peer on different grids.
    /// </summary>
    [Test]
    public void TeleportRequest_QuantizesPositionIdenticallyTo_PlayerState()
    {
        const float posX = 5.3f, posY = 73.1f, posZ = 11.9f;

        var teleport = new TeleportRequest
        {
            PositionXQuantized = posX, PositionYQuantized = posY, PositionZQuantized = posZ,
        };
        var state = new PlayerState
        {
            PositionXQuantized = posX, PositionYQuantized = posY, PositionZQuantized = posZ,
        };

        TeleportRequest t = TeleportRequest.Parser.ParseFrom(teleport.ToByteArray());
        PlayerState s = PlayerState.Parser.ParseFrom(state.ToByteArray());

        Assert.Multiple(() =>
        {
            Assert.That(t.PositionXQuantized, Is.EqualTo(s.PositionXQuantized), "position_x");
            Assert.That(t.PositionYQuantized, Is.EqualTo(s.PositionYQuantized), "position_y");
            Assert.That(t.PositionZQuantized, Is.EqualTo(s.PositionZQuantized), "position_z");
        });
    }
}
