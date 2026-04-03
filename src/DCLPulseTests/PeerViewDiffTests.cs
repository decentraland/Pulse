using Decentraland.Pulse;
using Pulse.Peers;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class PeerViewDiffTests
{
    private static readonly PeerIndex SUBJECT = new (1);

    private static PeerSnapshot MakeSnapshot(
        uint seq = 1,
        uint serverTick = 100,
        int parcel = 0,
        Vector3? localPosition = null,
        Vector3? velocity = null,
        float rotationY = 0f,
        int jumpCount = 0,
        float movementBlend = 0f,
        float slideBlend = 0f,
        float? headYaw = null,
        float? headPitch = null,
        PlayerAnimationFlags animationFlags = PlayerAnimationFlags.None,
        GlideState glideState = GlideState.PropClosed) =>
        new (
            Seq: seq,
            ServerTick: serverTick,
            Parcel: parcel,
            LocalPosition: localPosition ?? Vector3.Zero,
            GlobalPosition: localPosition ?? Vector3.Zero,
            Velocity: velocity ?? Vector3.Zero,
            RotationY: rotationY,
            JumpCount: jumpCount,
            MovementBlend: movementBlend,
            SlideBlend: slideBlend,
            HeadYaw: headYaw,
            HeadPitch: headPitch,
            AnimationFlags: animationFlags,
            GlideState: glideState);

    // ── Always returns a delta ──────────────────────────────────────

    [Test]
    public void CreateMessage_IdenticalSnapshots_ReturnsDelta()
    {
        PeerSnapshot from = MakeSnapshot(seq: 1);
        PeerSnapshot to = MakeSnapshot(seq: 2, serverTick: 200);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta, Is.Not.Null);
        Assert.That(delta.SubjectId, Is.EqualTo(SUBJECT.Value));
        Assert.That(delta.BaselineSeq, Is.EqualTo(1));
        Assert.That(delta.NewSeq, Is.EqualTo(2));
        Assert.That(delta.ServerTick, Is.EqualTo(200));
    }

    [Test]
    public void CreateMessage_IdenticalSnapshots_NoOptionalFieldsSet()
    {
        PeerSnapshot from = MakeSnapshot(seq: 1);
        PeerSnapshot to = MakeSnapshot(seq: 2);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasStateFlags, Is.False);
        Assert.That(delta.HasPositionX, Is.False);
        Assert.That(delta.HasPositionY, Is.False);
        Assert.That(delta.HasPositionZ, Is.False);
        Assert.That(delta.HasRotationY, Is.False);
        Assert.That(delta.HasVelocityX, Is.False);
        Assert.That(delta.HasVelocityY, Is.False);
        Assert.That(delta.HasVelocityZ, Is.False);
        Assert.That(delta.HasMovementBlend, Is.False);
        Assert.That(delta.HasSlideBlend, Is.False);
        Assert.That(delta.HasHeadYaw, Is.False);
        Assert.That(delta.HasHeadPitch, Is.False);
        Assert.That(delta.HasGlideState, Is.False);
        Assert.That(delta.HasParcelIndex, Is.False);
        Assert.That(delta.HasJumpCount, Is.False);
    }

    // ── StateFlags conditional ──────────────────────────────────────

    [Test]
    public void CreateMessage_AnimationFlagsChanged_SetStateFlags()
    {
        PeerSnapshot from = MakeSnapshot(animationFlags: PlayerAnimationFlags.None);
        PeerSnapshot to = MakeSnapshot(seq: 2, animationFlags: PlayerAnimationFlags.Grounded);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasStateFlags, Is.True);
        Assert.That(delta.StateFlags, Is.EqualTo((uint)PlayerAnimationFlags.Grounded));
    }

    [Test]
    public void CreateMessage_AnimationFlagsUnchanged_DoesNotSetStateFlags()
    {
        PeerSnapshot from = MakeSnapshot(animationFlags: PlayerAnimationFlags.Grounded);
        PeerSnapshot to = MakeSnapshot(seq: 2, animationFlags: PlayerAnimationFlags.Grounded);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasStateFlags, Is.False);
    }

    // ── Position diff ───────────────────────────────────────────────

    [Test]
    public void CreateMessage_PositionChanged_SetsPositionFields()
    {
        PeerSnapshot from = MakeSnapshot(localPosition: new Vector3(1f, 2f, 3f));
        PeerSnapshot to = MakeSnapshot(seq: 2, localPosition: new Vector3(5f, 6f, 7f));

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasPositionX, Is.True);
        Assert.That(delta.HasPositionY, Is.True);
        Assert.That(delta.HasPositionZ, Is.True);
    }

    [Test]
    public void CreateMessage_PositionWithinTolerance_DoesNotSetPositionFields()
    {
        PeerSnapshot from = MakeSnapshot(localPosition: new Vector3(1f, 2f, 3f));
        PeerSnapshot to = MakeSnapshot(seq: 2, localPosition: new Vector3(1.0005f, 2.0005f, 3.0005f));

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasPositionX, Is.False);
        Assert.That(delta.HasPositionY, Is.False);
        Assert.That(delta.HasPositionZ, Is.False);
    }

    // ── GlideState ──────────────────────────────────────────────────

    [Test]
    public void CreateMessage_GlideStateChanged_SetsGlideState()
    {
        PeerSnapshot from = MakeSnapshot(glideState: GlideState.PropClosed);
        PeerSnapshot to = MakeSnapshot(seq: 2, glideState: GlideState.Gliding);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_2);

        Assert.That(delta.HasGlideState, Is.True);
        Assert.That(delta.GlideState, Is.EqualTo(GlideState.Gliding));
    }

    [Test]
    public void CreateMessage_GlideStateUnchanged_DoesNotSetGlideState()
    {
        PeerSnapshot from = MakeSnapshot(glideState: GlideState.Gliding);
        PeerSnapshot to = MakeSnapshot(seq: 2, glideState: GlideState.Gliding);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasGlideState, Is.False);
    }

    // ── Tier-based field inclusion ──────────────────────────────────

    [Test]
    public void CreateMessage_Tier0_IncludesSlideBlendAndHeadIK()
    {
        PeerSnapshot from = MakeSnapshot();
        PeerSnapshot to = MakeSnapshot(seq: 2, slideBlend: 0.5f, headYaw: 45f, headPitch: -10f);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasSlideBlend, Is.True);
        Assert.That(delta.HasHeadYaw, Is.True);
        Assert.That(delta.HasHeadPitch, Is.True);
    }

    [Test]
    public void CreateMessage_Tier1_ExcludesSlideBlendAndHeadIK()
    {
        PeerSnapshot from = MakeSnapshot();
        PeerSnapshot to = MakeSnapshot(seq: 2, slideBlend: 0.5f, headYaw: 45f, headPitch: -10f);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_1);

        Assert.That(delta.HasSlideBlend, Is.False);
        Assert.That(delta.HasHeadYaw, Is.False);
        Assert.That(delta.HasHeadPitch, Is.False);
    }

    [Test]
    public void CreateMessage_Tier2_ExcludesSlideBlendAndHeadIK()
    {
        PeerSnapshot from = MakeSnapshot();
        PeerSnapshot to = MakeSnapshot(seq: 2, slideBlend: 0.5f, headYaw: 45f, headPitch: -10f);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_2);

        Assert.That(delta.HasSlideBlend, Is.False);
        Assert.That(delta.HasHeadYaw, Is.False);
        Assert.That(delta.HasHeadPitch, Is.False);
    }

    [Test]
    public void CreateMessage_Tier0_IncludesVelocityAndMovementBlend()
    {
        PeerSnapshot from = MakeSnapshot();
        PeerSnapshot to = MakeSnapshot(seq: 2, velocity: new Vector3(1f, 2f, 3f), movementBlend: 1.5f);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasVelocityX, Is.True);
        Assert.That(delta.HasVelocityY, Is.True);
        Assert.That(delta.HasVelocityZ, Is.True);
        Assert.That(delta.HasMovementBlend, Is.True);
    }

    [Test]
    public void CreateMessage_Tier1_IncludesVelocityAndMovementBlend()
    {
        PeerSnapshot from = MakeSnapshot();
        PeerSnapshot to = MakeSnapshot(seq: 2, velocity: new Vector3(1f, 2f, 3f), movementBlend: 1.5f);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_1);

        Assert.That(delta.HasVelocityX, Is.True);
        Assert.That(delta.HasVelocityY, Is.True);
        Assert.That(delta.HasVelocityZ, Is.True);
        Assert.That(delta.HasMovementBlend, Is.True);
    }

    [Test]
    public void CreateMessage_Tier2_ExcludesVelocityAndMovementBlend()
    {
        PeerSnapshot from = MakeSnapshot();
        PeerSnapshot to = MakeSnapshot(seq: 2, velocity: new Vector3(1f, 2f, 3f), movementBlend: 1.5f);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_2);

        Assert.That(delta.HasVelocityX, Is.False);
        Assert.That(delta.HasVelocityY, Is.False);
        Assert.That(delta.HasVelocityZ, Is.False);
        Assert.That(delta.HasMovementBlend, Is.False);
    }

    // ── Common fields across all tiers ──────────────────────────────

    [Test]
    public void CreateMessage_RotationChanged_SetAcrossAllTiers(
        [Values] PeerViewSimulationTierValue tierValue)
    {
        PeerSnapshot from = MakeSnapshot(rotationY: 0f);
        PeerSnapshot to = MakeSnapshot(seq: 2, rotationY: 90f);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, ToTier(tierValue));

        Assert.That(delta.HasRotationY, Is.True);
    }

    [Test]
    public void CreateMessage_ParcelChanged_SetAcrossAllTiers(
        [Values] PeerViewSimulationTierValue tierValue)
    {
        PeerSnapshot from = MakeSnapshot(parcel: 0);
        PeerSnapshot to = MakeSnapshot(seq: 2, parcel: 42);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, ToTier(tierValue));

        Assert.That(delta.HasParcelIndex, Is.True);
        Assert.That(delta.ParcelIndex, Is.EqualTo(42));
    }

    [Test]
    public void CreateMessage_JumpCountChanged_SetAcrossAllTiers(
        [Values] PeerViewSimulationTierValue tierValue)
    {
        PeerSnapshot from = MakeSnapshot(jumpCount: 0);
        PeerSnapshot to = MakeSnapshot(seq: 2, jumpCount: 3);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, ToTier(tierValue));

        Assert.That(delta.HasJumpCount, Is.True);
        Assert.That(delta.JumpCount, Is.EqualTo(3));
    }

    // ── HeadYaw/HeadPitch null-to-null stays unset ──────────────────

    [Test]
    public void CreateMessage_HeadYawBothNull_DoesNotSet()
    {
        PeerSnapshot from = MakeSnapshot(headYaw: null);
        PeerSnapshot to = MakeSnapshot(seq: 2, headYaw: null);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        Assert.That(delta.HasHeadYaw, Is.False);
    }

    [Test]
    public void CreateMessage_HeadYawToNull_DoesNotSet()
    {
        PeerSnapshot from = MakeSnapshot(headYaw: 10f);
        PeerSnapshot to = MakeSnapshot(seq: 2, headYaw: null);

        PlayerStateDeltaTier0 delta = PeerViewDiff.CreateMessage(SUBJECT, from, to, PeerViewSimulationTier.TIER_0);

        // FloatEquals(10f, null) is false, but to.HeadYaw.HasValue is false → guard prevents setting
        Assert.That(delta.HasHeadYaw, Is.False);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    public enum PeerViewSimulationTierValue : byte { Tier0 = 0, Tier1 = 1, Tier2 = 2 }

    private static PeerViewSimulationTier ToTier(PeerViewSimulationTierValue v) =>
        v switch
        {
            PeerViewSimulationTierValue.Tier0 => PeerViewSimulationTier.TIER_0,
            PeerViewSimulationTierValue.Tier1 => PeerViewSimulationTier.TIER_1,
            PeerViewSimulationTierValue.Tier2 => PeerViewSimulationTier.TIER_2,
            _ => throw new ArgumentOutOfRangeException(nameof(v)),
        };
}
