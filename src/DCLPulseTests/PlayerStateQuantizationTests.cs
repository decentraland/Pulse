using Decentraland.Pulse;

namespace DCLPulseTests;

[TestFixture]
public class PlayerStateQuantizationTests
{
    /// <summary>
    ///     PlayerState must quantize every shared field with the exact same options as
    ///     PlayerStateDeltaTier0, so a full state and a stream of deltas land on the same grid.
    ///     The two messages declare those options independently in the proto, so this locks them
    ///     against drift: set the same off-grid inputs on both and assert the decoded values match.
    ///     Each accessor decodes the stored uint32 code with its own field's options, so identical
    ///     options produce identical decoded values; a bits/min/max mismatch surfaces as a difference.
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

        Assert.Multiple(() =>
        {
            Assert.That(state.PositionXQuantized, Is.EqualTo(delta.PositionXQuantized), "position_x");
            Assert.That(state.PositionYQuantized, Is.EqualTo(delta.PositionYQuantized), "position_y");
            Assert.That(state.PositionZQuantized, Is.EqualTo(delta.PositionZQuantized), "position_z");
            Assert.That(state.VelocityXQuantized, Is.EqualTo(delta.VelocityXQuantized), "velocity_x");
            Assert.That(state.VelocityYQuantized, Is.EqualTo(delta.VelocityYQuantized), "velocity_y");
            Assert.That(state.VelocityZQuantized, Is.EqualTo(delta.VelocityZQuantized), "velocity_z");
            Assert.That(state.RotationYQuantized, Is.EqualTo(delta.RotationYQuantized), "rotation_y");
            Assert.That(state.MovementBlendQuantized, Is.EqualTo(delta.MovementBlendQuantized), "movement_blend");
            Assert.That(state.SlideBlendQuantized, Is.EqualTo(delta.SlideBlendQuantized), "slide_blend");
            Assert.That(state.HeadYawQuantized, Is.EqualTo(delta.HeadYawQuantized), "head_yaw");
            Assert.That(state.HeadPitchQuantized, Is.EqualTo(delta.HeadPitchQuantized), "head_pitch");
            Assert.That(state.PointAtXQuantized, Is.EqualTo(delta.PointAtXQuantized), "point_at_x");
            Assert.That(state.PointAtYQuantized, Is.EqualTo(delta.PointAtYQuantized), "point_at_y");
            Assert.That(state.PointAtZQuantized, Is.EqualTo(delta.PointAtZQuantized), "point_at_z");
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

        Assert.Multiple(() =>
        {
            Assert.That(teleport.PositionXQuantized, Is.EqualTo(state.PositionXQuantized), "position_x");
            Assert.That(teleport.PositionYQuantized, Is.EqualTo(state.PositionYQuantized), "position_y");
            Assert.That(teleport.PositionZQuantized, Is.EqualTo(state.PositionZQuantized), "position_z");
        });
    }
}
