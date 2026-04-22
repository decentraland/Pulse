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
public partial class PeerSimulationTests
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
    private EmoteCompleter emoteCompleter;
    private Dictionary<PeerIndex, PeerState> peers;

    /// <summary>
    ///     Mutable interest entries read by the single <see cref="IAreaOfInterest" /> callback.
    ///     Mutated by <see cref="SetVisibleSubjects" /> to control what the mock returns.
    /// </summary>
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
            profileBoard, Substitute.For<ILogger<PeerSimulation>>());

        emoteCompleter = new EmoteCompleter(snapshotBoard, timeProvider);

        peers = new Dictionary<PeerIndex, PeerState>
        {
            [observer] = new (PeerConnectionState.AUTHENTICATED),
        };

        // Publish snapshots so TryRead succeeds
        PublishSnapshot(observer, seq: 1);
        PublishSnapshot(subject, seq: 1);

        identityBoard.Set(subject, "0xSUBJECT_WALLET");
    }

    // ── Helpers ──────────────────────────────────────────────────────

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
        // Production EmoteStartHandler stamps the snapshot Seq into Emote.StartSeq so the scan's
        // real-start discriminator (Seq == StartSeq) identifies the event unambiguously. Match
        // that here so tests exercise the production shape.
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

    private void PublishEmoteStopSnapshot(PeerIndex peer, uint seq, uint emoteStartTick = 0, uint emoteStartSeq = 0,
        EmoteStopReason reason = EmoteStopReason.Cancelled, Vector3? position = null)
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
            GlideState: GlideState.PropClosed,
            Emote: new EmoteState(null, StartSeq: emoteStartSeq, StartTick: emoteStartTick, StopReason: reason)));
    }

    private void AddResyncRequest(PeerIndex observerPeer, PeerIndex subjectPeer, uint knownSeq)
    {
        PeerState state = peers[observerPeer];
        state.ResyncRequests ??= new Dictionary<PeerIndex, uint>();
        state.ResyncRequests[subjectPeer] = knownSeq;
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
