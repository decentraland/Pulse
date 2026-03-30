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
        Assert.That(msg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));
        Assert.That(msg.Message.PlayerJoined.UserId, Is.EqualTo("0xSUBJECT_WALLET"));
        Assert.That(msg.Message.PlayerJoined.State.SubjectId, Is.EqualTo(subject.Value));
    }

    [Test]
    public void PlayerJoined_ContainsFullState()
    {
        var snapshot = new PeerSnapshot(
            Seq: 5, ServerTick: 42,
            Parcel: 0,
            LocalPosition: new Vector3(1.0f, 2.0f, 3.0f),
            GlobalPosition: new Vector3(1.0f, 2.0f, 3.0f),
            Velocity: new Vector3(0.5f, 0f, -0.5f),
            RotationY: 1.57f,
            MovementBlend: 0.8f, SlideBlend: 0.2f,
            JumpCount: 0,
            HeadYaw: 0.3f, HeadPitch: -0.1f,
            AnimationFlags: PlayerAnimationFlags.Grounded,
            GlideState: GlideState.PropClosed);

        snapshotBoard.Publish(subject, snapshot);
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);

        OutgoingMessage msg = DrainSingleMessage();
        PlayerStateFull state = msg.Message.PlayerJoined.State;
        Assert.That(state.Sequence, Is.EqualTo(5u));
        Assert.That(state.ServerTick, Is.EqualTo(42u));
        Assert.That(state.State.Position.X, Is.EqualTo(1.0f));
        Assert.That(state.State.Position.Y, Is.EqualTo(2.0f));
        Assert.That(state.State.Position.Z, Is.EqualTo(3.0f));
        Assert.That(state.State.RotationY, Is.EqualTo(1.57f));
        Assert.That(state.State.MovementBlend, Is.EqualTo(0.8f));
        Assert.That(state.State.StateFlags, Is.EqualTo((uint)PlayerAnimationFlags.Grounded));
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
