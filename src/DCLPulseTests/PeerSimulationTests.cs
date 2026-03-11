using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

[TestFixture]
public class PeerSimulationTests
{
    private const int MAX_PEERS = 100;
    private const int RING_CAPACITY = 10;

    /// <summary>
    ///     SWEEP_INTERVAL is a private const in PeerSimulation (100).
    ///     Tests that depend on sweep timing use this value.
    /// </summary>
    private const uint SWEEP_INTERVAL = 100;

    private static readonly uint[] SimulationSteps = [50u, 100u, 200u];

    private PeerIndex observer;
    private PeerIndex subject;

    private SnapshotBoard snapshotBoard;
    private IdentityBoard identityBoard;
    private MessagePipe messagePipe;
    private IAreaOfInterest areaOfInterest;
    private ITimeProvider timeProvider;
    private PeerSimulation simulation;
    private Dictionary<PeerIndex, PeerState> peers;

    /// <summary>
    ///     Mutable interest entries read by the single <see cref="IAreaOfInterest" /> callback.
    ///     Mutated by <see cref="SetVisibleSubjects" /> to control what the mock returns.
    /// </summary>
    private List<(PeerIndex Subject, PeerViewSimulationTier Tier)> visibleSubjects;

    [SetUp]
    public void SetUp()
    {
        observer = new PeerIndex(0);
        subject = new PeerIndex(1);

        snapshotBoard = new SnapshotBoard(MAX_PEERS, RING_CAPACITY);
        identityBoard = new IdentityBoard(MAX_PEERS);
        messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>());
        areaOfInterest = Substitute.For<IAreaOfInterest>();
        visibleSubjects = new List<(PeerIndex, PeerViewSimulationTier)>();

        areaOfInterest.When(x => x.GetVisibleSubjects(
                           Arg.Any<PeerIndex>(), Arg.Any<PeerSnapshot>(), Arg.Any<IInterestCollector>()))
                      .Do(ci =>
                       {
                           IInterestCollector? collector = ci.ArgAt<IInterestCollector>(2);

                           foreach ((PeerIndex s, PeerViewSimulationTier t) in visibleSubjects)
                               collector.Add(s, t);
                       });

        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(0u);

        simulation = new PeerSimulation(
            areaOfInterest, snapshotBoard, identityBoard, messagePipe,
            SimulationSteps, timeProvider);

        peers = new Dictionary<PeerIndex, PeerState>
        {
            [observer] = new (PeerConnectionState.AUTHENTICATED),
        };

        // Publish snapshots so TryRead succeeds
        PublishSnapshot(observer, seq: 1);
        PublishSnapshot(subject, seq: 1);

        identityBoard.Set(subject, "0xSUBJECT_WALLET");
    }

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
            Position: new Vector3(1.0f, 2.0f, 3.0f),
            Velocity: new Vector3(0.5f, 0f, -0.5f),
            RotationY: 1.57f,
            MovementBlend: 0.8f, SlideBlend: 0.2f,
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
    public void PlayerLeft_SentWhenSubjectLeavesInterestSet()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject disappears from interest set
        SetVisibleSubjects();

        // Sweep fires at multiples of SWEEP_INTERVAL. The stale check is strict greater-than,
        // so the first sweep that catches a view stamped at tick 0 is tick 2*SWEEP_INTERVAL.
        simulation.SimulateTick(peers, tickCounter: SWEEP_INTERVAL * 2);

        OutgoingMessage msg = DrainSingleMessage();
        Assert.That(msg.To, Is.EqualTo(observer));
        Assert.That(msg.PacketMode, Is.EqualTo(ITransport.PacketMode.RELIABLE));
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerLeft));
        Assert.That(msg.Message.PlayerLeft.SubjectId, Is.EqualTo(subject.Value));
    }

    [Test]
    public void PlayerLeft_NotSentIfSubjectReappearsBeforeSweep()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Subject disappears for a few ticks
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: 1);
        simulation.SimulateTick(peers, tickCounter: 2);

        // Subject reappears before sweep would catch the stale view
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        PublishSnapshot(subject, seq: 2);

        // Re-stamp the view on a tick before the sweep
        simulation.SimulateTick(peers, tickCounter: (SWEEP_INTERVAL * 2) - 1);
        DrainAllMessages();

        // Sweep fires — view was re-stamped recently so it should not be swept
        simulation.SimulateTick(peers, tickCounter: SWEEP_INTERVAL * 2);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages.Any(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft), Is.False);
    }

    [Test]
    public void PlayerLeft_SentForMultipleSubjects()
    {
        var subject2 = new PeerIndex(2);
        PublishSnapshot(subject2, seq: 1);
        identityBoard.Set(subject2, "0xSUBJECT2_WALLET");

        SetVisibleSubjects(
            (subject, PeerViewSimulationTier.TIER_0),
            (subject2, PeerViewSimulationTier.TIER_0));

        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        // Both subjects leave
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: SWEEP_INTERVAL * 2);

        List<OutgoingMessage> messages = DrainAllMessages();

        var leftIds = messages
                     .Where(m => m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerLeft)
                     .Select(m => m.Message.PlayerLeft.SubjectId)
                     .ToList();

        Assert.That(leftIds, Has.Count.EqualTo(2));
        Assert.That(leftIds, Does.Contain(subject.Value));
        Assert.That(leftIds, Does.Contain(subject2.Value));
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

    // ── Helpers ──────────────────────────────────────────────────────

    private void PublishSnapshot(PeerIndex peer, uint seq)
    {
        snapshotBoard.SetActive(peer);

        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: seq, ServerTick: seq * 10,
            Parcel: 0,
            Position: Vector3.Zero, Velocity: Vector3.Zero,
            RotationY: 0f, MovementBlend: 0f, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed));
    }

    private void SetVisibleSubjects(params (PeerIndex Subject, PeerViewSimulationTier Tier)[] entries)
    {
        visibleSubjects.Clear();
        visibleSubjects.AddRange(entries);
    }

    private OutgoingMessage DrainSingleMessage()
    {
        Assert.That(messagePipe.TryReadOutgoingMessage(out OutgoingMessage msg), Is.True,
            "Expected a message but the outgoing channel was empty");

        Assert.That(messagePipe.TryReadOutgoingMessage(out _), Is.False,
            "Expected exactly one message but found more");

        return msg;
    }

    private List<OutgoingMessage> DrainAllMessages()
    {
        var messages = new List<OutgoingMessage>();

        while (messagePipe.TryReadOutgoingMessage(out OutgoingMessage msg))
            messages.Add(msg);

        return messages;
    }
}
