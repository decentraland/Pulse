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
public class SelfMirrorTests
{
    private const int MAX_PEERS = 100;
    private const int RING_CAPACITY = 10;
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
    private List<(PeerIndex Subject, PeerViewSimulationTier Tier)> visibleSubjects;
    private SpatialGrid spatialGrid;
    private ProfileBoard profileBoard;

    [SetUp]
    public void SetUp()
    {
        observer = new PeerIndex(0);
        subject = new PeerIndex(1);

        snapshotBoard = new SnapshotBoard(MAX_PEERS, RING_CAPACITY);
        identityBoard = new IdentityBoard(MAX_PEERS);
        spatialGrid = new SpatialGrid(50, MAX_PEERS);
        messagePipe = new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10));
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

        profileBoard = new ProfileBoard(MAX_PEERS);

        simulation = new PeerSimulation(
            areaOfInterest, snapshotBoard, spatialGrid, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(),
            profileBoard, Substitute.For<ILogger<PeerSimulation>>(),
            selfMirrorEnabled: true, selfMirrorTier: 0);

        peers = new Dictionary<PeerIndex, PeerState>
        {
            [observer] = new (PeerConnectionState.AUTHENTICATED),
        };

        PublishSnapshot(observer, seq: 1);
        PublishSnapshot(subject, seq: 1);

        identityBoard.Set(observer, "0xOBSERVER_WALLET");
        identityBoard.Set(subject, "0xSUBJECT_WALLET");
    }

    private void PublishSnapshot(PeerIndex peer, uint seq, Vector3? position = null)
    {
        snapshotBoard.SetActive(peer);

        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: seq, ServerTick: seq * 10,
            Parcel: 0,
            LocalPosition: position ?? Vector3.Zero, Velocity: Vector3.Zero,
            GlobalPosition: position ?? Vector3.Zero,
            RotationY: 0f, MovementBlend: 0f, JumpCount: 0, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed));
    }

    private void PublishEmoteSnapshot(PeerIndex peer, uint seq, string emoteId = "wave",
        uint? durationMs = null, Vector3? position = null, uint? startTick = null)
    {
        uint tick = startTick ?? seq * 10;
        snapshotBoard.SetActive(peer);

        snapshotBoard.Publish(peer, new PeerSnapshot(
            Seq: seq, ServerTick: tick,
            Parcel: 0,
            LocalPosition: position ?? Vector3.Zero, Velocity: Vector3.Zero,
            GlobalPosition: position ?? Vector3.Zero,
            RotationY: 0f, MovementBlend: 0f, JumpCount: 0, SlideBlend: 0f,
            HeadYaw: null, HeadPitch: null,
            AnimationFlags: PlayerAnimationFlags.None,
            GlideState: GlideState.PropClosed,
            Emote: new EmoteState(emoteId, StartSeq: seq, StartTick: tick, DurationMs: durationMs)));
    }

    private void SetVisibleSubjects(params (PeerIndex Subject, PeerViewSimulationTier Tier)[] entries)
    {
        visibleSubjects.Clear();
        visibleSubjects.AddRange(entries);
    }

    private List<OutgoingMessage> DrainAllMessages()
    {
        var messages = new List<OutgoingMessage>();

        while (messagePipe.TryReadOutgoingMessage(out OutgoingMessage msg))
            messages.Add(msg);

        return messages;
    }

    [Test]
    public void SelfMirror_SendsPlayerJoinedWithMirrorWalletId()
    {
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: 0);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages, Has.Count.EqualTo(1));

        OutgoingMessage msg = messages[0];
        Assert.That(msg.To, Is.EqualTo(observer));
        Assert.That(msg.PacketMode, Is.EqualTo(PacketMode.RELIABLE));
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerJoined));
        Assert.That(msg.Message.PlayerJoined.UserId, Is.EqualTo(PeerSimulation.SELF_MIRROR_WALLET_ID));
        Assert.That(msg.Message.PlayerJoined.State.SubjectId, Is.EqualTo(observer.Value));
    }

    [Test]
    public void SelfMirror_SendsDeltaOnSubsequentTicks()
    {
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishSnapshot(observer, seq: 2, position: new Vector3(5, 0, 0));
        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages, Has.Count.EqualTo(1));

        OutgoingMessage msg = messages[0];
        Assert.That(msg.To, Is.EqualTo(observer));
        Assert.That(msg.PacketMode, Is.EqualTo(PacketMode.UNRELIABLE_SEQUENCED));
        Assert.That(msg.Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
    }

    [Test]
    public void SelfMirror_CoexistsWithRealSubjects()
    {
        SetVisibleSubjects((subject, PeerViewSimulationTier.TIER_0));
        simulation.SimulateTick(peers, tickCounter: 0);

        List<OutgoingMessage> messages = DrainAllMessages();

        Assert.That(messages.Count(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerJoined), Is.EqualTo(2));

        OutgoingMessage mirrorMsg = messages.First(m =>
            m.Message.PlayerJoined?.UserId == PeerSimulation.SELF_MIRROR_WALLET_ID);

        Assert.That(mirrorMsg.Message.PlayerJoined.State.SubjectId, Is.EqualTo(observer.Value));

        OutgoingMessage subjectMsg = messages.First(m =>
            m.Message.PlayerJoined?.UserId == "0xSUBJECT_WALLET");

        Assert.That(subjectMsg.Message.PlayerJoined.State.SubjectId, Is.EqualTo(subject.Value));
    }

    [Test]
    public void SelfMirror_MirrorsEmoteEvents()
    {
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        timeProvider.MonotonicTime.Returns(100u);
        PublishEmoteSnapshot(observer, seq: 2, startTick: 100, durationMs: 2000);

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();

        Assert.That(messages.Select(m => m.Message.MessageCase).ToList(),
            Has.Member(ServerMessage.MessageOneofCase.EmoteStarted));

        OutgoingMessage emoteMsg = messages.First(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.EmoteStarted);

        Assert.That(emoteMsg.To, Is.EqualTo(observer));
        Assert.That(emoteMsg.Message.EmoteStarted.EmoteId, Is.EqualTo("wave"));
        Assert.That(emoteMsg.Message.EmoteStarted.SubjectId, Is.EqualTo(observer.Value));
    }

    [Test]
    public void SelfMirror_MirrorsProfileAnnouncements()
    {
        SetVisibleSubjects();
        simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        profileBoard.Set(observer, 42);
        PublishSnapshot(observer, seq: 2, position: new Vector3(1, 0, 0));

        simulation.SimulateTick(peers, tickCounter: 1);

        List<OutgoingMessage> messages = DrainAllMessages();

        OutgoingMessage profileMsg = messages.First(m =>
            m.Message.MessageCase == ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced);

        Assert.That(profileMsg.To, Is.EqualTo(observer));
        Assert.That(profileMsg.Message.PlayerProfileVersionAnnounced.Version, Is.EqualTo(42));
    }

    [Test]
    public void SelfMirror_DisabledByDefault_SkipsSelf()
    {
        var disabledSimulation = new PeerSimulation(
            areaOfInterest, snapshotBoard, spatialGrid, identityBoard, messagePipe,
            SimulationSteps, timeProvider,
            Substitute.For<ITransport>(),
            profileBoard, Substitute.For<ILogger<PeerSimulation>>());

        SetVisibleSubjects((observer, PeerViewSimulationTier.TIER_0));

        disabledSimulation.SimulateTick(peers, tickCounter: 0);

        Assert.That(messagePipe.TryReadOutgoingMessage(out _), Is.False);
    }

    [Test]
    public void SelfMirror_RespectsTierFrequency()
    {
        var tier1Simulation = new PeerSimulation(
            areaOfInterest, snapshotBoard, spatialGrid, identityBoard, messagePipe,
            SimulationSteps, timeProvider, Substitute.For<ITransport>(),
            profileBoard,
            Substitute.For<ILogger<PeerSimulation>>(),
            selfMirrorEnabled: true, selfMirrorTier: 1);

        SetVisibleSubjects();

        tier1Simulation.SimulateTick(peers, tickCounter: 0);
        DrainAllMessages();

        PublishSnapshot(observer, seq: 2, position: new Vector3(5, 0, 0));

        tier1Simulation.SimulateTick(peers, tickCounter: 1);
        Assert.That(messagePipe.TryReadOutgoingMessage(out _), Is.False);

        tier1Simulation.SimulateTick(peers, tickCounter: 2);
        List<OutgoingMessage> messages = DrainAllMessages();
        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages[0].Message.MessageCase, Is.EqualTo(ServerMessage.MessageOneofCase.PlayerStateDelta));
    }
}
