using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

public partial class PeerSimulationTests
{
    [Test]
    public void PlayerJoined_SentWhenSubjectFirstAppears()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);

        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.To, Is.EqualTo(observer));
        Assert.That(msg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));
        Assert.That(msg.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
        Assert.That(msg.Message.PlayerJoined.State.SubjectId, Is.EqualTo(subject.Value));
    }

    [Test]
    public void PlayerJoined_ContainsFullState()
    {
        var snapshot = TestSnapshots.Make(
            seq: 5, serverTick: 42,
            parcel: 0,
            position: new Vector3(1.0f, 2.0f, 3.0f),
            velocity: new Vector3(0.5f, 0f, -0.5f),
            rotationY: 1.57f,
            movementBlend: 0.8f, slideBlend: 0.2f,
            jumpCount: 2,
            headYaw: 0.3f, headPitch: -0.1f,
            animationFlags: PlayerAnimationFlags.Grounded,
            glideState: GlideState.PropClosed);

        snapshotBoard.Publish(subject, snapshot);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);

        OutgoingMessage msg = DrainSingleMessage();
        PlayerStateFull state = msg.Message.PlayerJoined.State;
        Assert.That(state.Sequence, Is.EqualTo(5u));
        Assert.That(state.ServerTick, Is.EqualTo(42u));
        Assert.That(state.State.PositionXQuantized, Is.EqualTo(1.0f).Within(PlayerState.PositionXQuantizedStep));
        Assert.That(state.State.PositionYQuantized, Is.EqualTo(2.0f).Within(PlayerState.PositionYQuantizedStep));
        Assert.That(state.State.PositionZQuantized, Is.EqualTo(3.0f).Within(PlayerState.PositionZQuantizedStep));
        Assert.That(state.State.RotationYQuantized, Is.EqualTo(1.57f).Within(PlayerState.RotationYQuantizedStep));
        Assert.That(state.State.MovementBlendQuantized, Is.EqualTo(0.8f).Within(PlayerState.MovementBlendQuantizedStep));
        Assert.That(state.State.StateFlags, Is.EqualTo((uint)PlayerAnimationFlags.Grounded));
        Assert.That(state.State.JumpCount, Is.EqualTo(2));
    }

    [Test]
    public void PlayerJoined_NotSentOnSubsequentTicks()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages(); // consume the PlayerJoined

        // Same snapshot, same subject — not new anymore
        simulation.SimulateTick(peers, tickCounter: 1);

        Assert.That(messagePipe.TryReadOutgoingMessage(out _), Is.False);
    }

    [Test]
    public void PlayerJoined_NotSentForSelf()
    {
        // Observer sees itself — should be skipped
        SetVisibleSubjects((observer, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);

        Assert.That(messagePipe.TryReadOutgoingMessage(out _), Is.False);
    }

    [Test]
    public void PlayerJoined_NextDeltaRespectsTickDue_Tier2()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_2));

        // Tick 0: TIER_2 divisor is 4, tick 0 % 4 == 0 → PlayerJoined fires
        simulation.SimulateTick(peers, tickCounter: 0);
        OutgoingMessage joinMsg = DrainSingleMessage();
        Assert.That(joinMsg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));

        // Subject moves — snapshot advances
        PublishSnapshot(subject, seq: 2, position: new Vector3(5f, 0f, 0f));

        // Ticks 1, 2, 3: not divisible by 4 → tier gate blocks, no delta
        for (uint tick = 1; tick <= 3; tick++)
        {
            simulation.SimulateTick(peers, tickCounter: tick);

            Assert.That(messagePipe.TryReadOutgoingMessage(out _), Is.False,
                $"Delta leaked on tick {tick} which is not due for TIER_2");
        }

        // Tick 4: 4 % 4 == 0 → delta fires
        simulation.SimulateTick(peers, tickCounter: 4);
        OutgoingMessage deltaMsg = DrainSingleMessage();
        Assert.That(deltaMsg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
        Assert.That(deltaMsg.PacketMode, Is.EqualTo(PacketMode.UNRELIABLE_SEQUENCED));
    }

    [Test]
    public void PlayerJoined_SentAgainAfterPlayerLeftAndReentry()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject leaves and gets swept
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: SWEEP_INTERVAL * 2);
        DrainAllMessages();

        // Subject re-enters
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        PublishSnapshot(subject, seq: 3);
        simulation.SimulateTick(peers, tickCounter: (SWEEP_INTERVAL * 2) + 1);

        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));
        Assert.That(msg.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
    }
}
