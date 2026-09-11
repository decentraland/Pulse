using Decentraland.Pulse;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Presence;
using Pulse.Transport;
using Pulse.Transport.Hardening;
using System.Buffers;
using System.Numerics;
using System.Threading.Channels;
using static Pulse.Messaging.MessagePipe;

namespace DCLPulseTests;

/// <summary>
///     A live presence pipeline: real boards, real <see cref="ClusterTracker" />, real
///     <see cref="ParcelChangeTracker" /> and the real <see cref="NatsPublisher" /> outbox and
///     serializer, with only the broker and the clock replaced. Tests drive peers the way the server
///     does — place, move, teleport, remove — and read back the batches the wire would carry, so the
///     bytes they assert on came through every step the deployed feed goes through.
/// </summary>
internal sealed class PresenceScenario
{
    public const int MAX_PEERS = 32;
    private const float CELL_SIZE = 100f;
    private const int PARCEL_SIZE = 16;

    private static readonly uint[] SIMULATION_STEPS = [50u, 100u, 200u];

    /// <summary>
    ///     Phase-2 gate. Short so <see cref="DriveCleanup" /> can step past it without the monotonic
    ///     clock running away from the wall-clock stamps the fixtures pin.
    /// </summary>
    private const uint DISCONNECTION_CLEAN_TIMEOUT_MS = 100;

    /// <summary>
    ///     <paramref name="unixOriginMs" /> is the wall clock this process started at, and defaults to
    ///     the contract pack's T0 so that every timestamp a test pins — a batch's <c>server_time</c>, a
    ///     peer's <c>lastPing</c>, a pass's <c>lastUpdated</c> — is expressible as a monotonic offset
    ///     from it, which is the only way the two clocks can agree the way they do in production.
    /// </summary>
    public PresenceScenario(
        string serverName = "pulse-1",
        int channelCapacity = 1024,
        long unixOriginMs = IterationTwoFixtures.T0)
    {
        Clock = new TestTimeProvider { UnixOriginMs = unixOriginMs };
        Grids = new RealmSpatialGrids(CELL_SIZE, MAX_PEERS);
        SnapshotBoard = new SnapshotBoard(MAX_PEERS, ringCapacity: 8);
        IdentityBoard = new IdentityBoard(MAX_PEERS);
        ClusterBoard = new ClusterBoard();
        ParcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));

        var natsOptions = Options.Create(new NatsOptions
        {
            // Any parseable broker: nothing connects, since the run loop is never started — the
            // outbox and the batch assembly are what these tests drive.
            Url = "nats://localhost:4222",
            ServerName = serverName,
            ChannelCapacity = channelCapacity,
        });

        IOptions<PresenceOptions> presenceOptions = Options.Create(new PresenceOptions());

        Publisher = new NatsPublisher(
            NullLogger<NatsPublisher>.Instance, NullLoggerFactory.Instance,
            natsOptions, presenceOptions, Clock, SnapshotBoard);

        ParcelChanges = new ParcelChangeTracker(Publisher, ParcelEncoder, presenceOptions, natsOptions, MAX_PEERS);

        Tracker = new ClusterTracker(
            NullLogger<ClusterTracker>.Instance,
            Options.Create(new ClusterOptions { Enabled = true, PassIntervalMs = 1000, DwellPasses = 1, IdPrefix = "C" }),
            Grids, SnapshotBoard, IdentityBoard, ClusterBoard, Publisher, ParcelChanges, Clock, MAX_PEERS);

        SnapshotPublisher = new PeerSnapshotPublisher(SnapshotBoard, Grids, ParcelEncoder, Clock);
        Transport = Substitute.For<ITransport>();

        FieldValidator = new FieldValidator(
            Options.Create(new FieldValidatorOptions { MaxRealmLength = 255, MaxEmoteDurationMs = 60_000 }),
            Options.Create(new SceneListenerOptions()),
            ParcelEncoder,
            SceneListenerTestFactory.CellMapper(),
            Transport);

        TeleportHandler = new TeleportHandler(
            NullLogger<TeleportHandler>.Instance,
            SnapshotBoard,
            SnapshotPublisher,
            new DiscreteEventRateLimiter(
                Options.Create(new DiscreteEventRateLimiterOptions { RatePerSecond = 0 }), Clock, Transport),
            FieldValidator);

        ProfileBoard = new ProfileBoard(MAX_PEERS);
        PeerIndexAllocator = Substitute.For<IPeerIndexAllocator>();
        MessagePipe = new MessagePipe(NullLogger<MessagePipe>.Instance, new ServerMessageCounters());

        Simulation = new PeerSimulation(
            Substitute.For<IAreaOfInterest>(), SnapshotBoard, Grids, IdentityBoard, MessagePipe,
            SIMULATION_STEPS, Clock, Transport, ProfileBoard, PeerIndexAllocator,
            NullLogger<PeerSimulation>.Instance, ParcelChanges,
            disconnectionCleanTimeoutMs: DISCONNECTION_CLEAN_TIMEOUT_MS);
    }

    public TestTimeProvider Clock { get; }

    public RealmSpatialGrids Grids { get; }

    public SnapshotBoard SnapshotBoard { get; }

    public IdentityBoard IdentityBoard { get; }

    public ClusterBoard ClusterBoard { get; }

    public ParcelEncoder ParcelEncoder { get; }

    public NatsPublisher Publisher { get; }

    public ParcelChangeTracker ParcelChanges { get; }

    public ClusterTracker Tracker { get; }

    public PeerSnapshotPublisher SnapshotPublisher { get; }

    public FieldValidator FieldValidator { get; }

    public TeleportHandler TeleportHandler { get; }

    public ITransport Transport { get; }

    public ProfileBoard ProfileBoard { get; }

    public IPeerIndexAllocator PeerIndexAllocator { get; }

    public MessagePipe MessagePipe { get; }

    /// <summary>
    ///     The real <see cref="PeerSimulation" />, wired to this scenario's boards and to the
    ///     publishing <see cref="ParcelChanges" />, so an exit reaches the feed through the production
    ///     cleanup rather than through a direct call.
    /// </summary>
    public PeerSimulation Simulation { get; }

    /// <summary>The worker's peer set, as <c>PeersManager</c> keeps it.</summary>
    public Dictionary<PeerIndex, PeerState> Peers { get; } = new ();

    /// <summary>
    ///     The middle of a parcel, so nothing lands on a grid-cell boundary — see
    ///     <c>ClusterTrackerTests.PublishingOnACellBoundary_CanLandInTheLowerCell</c>.
    /// </summary>
    public static Vector3 CentreOf(int parcelX, int parcelY) =>
        new ((parcelX + 0.5f) * PARCEL_SIZE, 0f, (parcelY + 0.5f) * PARCEL_SIZE);

    /// <summary>
    ///     Registers a peer's wallet and puts it in a realm at a parcel, as authentication plus the
    ///     first snapshot publish would.
    /// </summary>
    public void Place(PeerIndex peer, string wallet, string realm, int parcelX, int parcelY, Vector3? position = null)
    {
        IdentityBoard.Set(peer, wallet);
        SnapshotBoard.SetActive(peer);
        Move(peer, realm, parcelX, parcelY, position);
    }

    /// <summary>
    ///     Moves a peer to a parcel, publishing the snapshot and re-indexing the grid the way
    ///     <see cref="PeerSnapshotPublisher" /> does — but with the exact global position the caller
    ///     asked for, since the production path quantizes the in-parcel offset to a step far coarser
    ///     than the goldens' tolerance.
    /// </summary>
    public void Move(PeerIndex peer, string realm, int parcelX, int parcelY, Vector3? position = null)
    {
        Vector3 at = position ?? CentreOf(parcelX, parcelY);

        Grids.Set(peer, realm, at);

        SnapshotBoard.Publish(peer, TestSnapshots.Make(
            seq: SnapshotBoard.LastSeq(peer) + 1,
            serverTick: Clock.MonotonicTime,
            parcel: ParcelEncoder.Encode(parcelX, parcelY),
            globalPosition: at,
            realm: realm));
    }

    /// <summary>
    ///     Moves a peer through the production ingest path — <see cref="FieldValidator" /> then
    ///     <see cref="PeerSnapshotPublisher" /> — so a test can assert what the realm canonicalizer
    ///     does to a realm that arrives mixed-case.
    /// </summary>
    public void Teleport(PeerIndex peer, string realm, int parcelX, int parcelY)
    {
        var peers = new Dictionary<PeerIndex, PeerState>
        {
            [peer] = new (PeerConnectionState.AUTHENTICATED),
        };

        TeleportHandler.Handle(peers, peer, new ClientMessage
        {
            Teleport = new TeleportRequest
            {
                Realm = realm,
                ParcelIndex = ParcelEncoder.Encode(parcelX, parcelY),
                PositionXQuantized = PARCEL_SIZE / 2f,
                PositionYQuantized = 0f,
                PositionZQuantized = PARCEL_SIZE / 2f,
            },
        });
    }

    /// <summary>
    ///     Takes a peer out the way every exit path does: the <c>Disconnected</c> lifecycle event
    ///     drained by the real <c>PeersManager</c> (phase 1 — off the grid, off the board, state
    ///     DISCONNECTING), then a simulation tick past
    ///     <see cref="DISCONNECTION_CLEAN_TIMEOUT_MS" /> so the real
    ///     <see cref="PeerSimulation" /> cleanup runs (phase 2 — the presence feed's exit seam).
    ///     <para />
    ///     Whatever kicked the peer — a clean disconnect, an auth timeout, a duplicate-session kick, a
    ///     ban, <c>PeerDefense</c> — reaches the feed through exactly this, because all of them are a
    ///     transport disconnect and the transport raises one lifecycle event for each.
    /// </summary>
    public void Remove(PeerIndex peer)
    {
        DispatchDisconnected(peer);
        DriveCleanup();
    }

    /// <summary>
    ///     Phase 1 alone: the lifecycle event a transport disconnect produces, drained on the owning
    ///     worker against this scenario's peer set.
    /// </summary>
    public void DispatchDisconnected(PeerIndex peer) =>
        Dispatch(IncomingEvent.Disconnected(peer));

    /// <summary>
    ///     The <c>Connected</c> lifecycle event, which is what puts a peer in <c>PENDING_AUTH</c> and
    ///     stamps the connection time the auth timeout is measured from — so a test of that timeout
    ///     starts from the state production starts from rather than a hand-built one.
    /// </summary>
    public void DispatchConnected(PeerIndex peer) =>
        Dispatch(IncomingEvent.Connected(peer));

    private void Dispatch(IncomingEvent evt)
    {
        Channel<IncomingEvent> events = Channel.CreateUnbounded<IncomingEvent>();
        events.Writer.TryWrite(evt);

        CreatePeersManager().DrainEvents(events.Reader, Peers, workerIndex: 0);
    }

    /// <summary>
    ///     Phase 2 alone: the clock steps past the cleanup gate and one tick runs, which is what
    ///     releases the peer and publishes its exit.
    /// </summary>
    public void DriveCleanup()
    {
        Clock.MonotonicTime += DISCONNECTION_CLEAN_TIMEOUT_MS;

        Simulation.SimulateTick(Peers, tickCounter: Clock.MonotonicTime / SIMULATION_STEPS[0]);
    }

    /// <summary>
    ///     Registers a peer as connected-and-authenticated in the worker's peer set, so the lifecycle
    ///     paths that read <see cref="PeerState" /> see what they would in production.
    /// </summary>
    public void Authenticate(PeerIndex peer)
    {
        Peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
    }

    private PeersManager CreatePeersManager() =>
        new (
            MessagePipe, new PeerStateFactory(), Substitute.For<IAreaOfInterest>(), SnapshotBoard, Grids,
            IdentityBoard, new PeerOptions(), NullLogger<PeersManager>.Instance,
            NullLogger<PeerSimulation>.Instance, Clock,
            new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>(),
            Transport, ProfileBoard, new ClientMessageCounters(),
            new EmoteCompleter(SnapshotBoard, Clock), PeerIndexAllocator,
            new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions
            {
                PreAuthBudget = 0, MaxConcurrentPreAuthPerIP = 0,
            })),
            DisabledIpLimiter(),
            ParcelChanges);

    /// <summary>
    ///     Cap switched off — the limiter counts connections but refuses none, so it never interferes
    ///     with the lifecycle path under test.
    /// </summary>
    private static IpLimiter DisabledIpLimiter()
    {
        IOptionsMonitor<IpLimiterOptions> optionsMonitor = Substitute.For<IOptionsMonitor<IpLimiterOptions>>();
        optionsMonitor.CurrentValue.Returns(new IpLimiterOptions { Enabled = false, MaxConcurrency = 0 });
        optionsMonitor.OnChange(Arg.Any<Action<IpLimiterOptions, string?>>()).Returns(Substitute.For<IDisposable>());

        return new IpLimiter(optionsMonitor, NullLogger<IpLimiter>.Instance);
    }

    public void RunPass() =>
        Tracker.RunPass();

    /// <summary>
    ///     Registers a peer's wallet and marks it active without placing it, for the paths where the
    ///     first placement itself is what a test drives — the mixed-case handshake, above all.
    /// </summary>
    public void Register(PeerIndex peer, string wallet)
    {
        IdentityBoard.Set(peer, wallet);
        SnapshotBoard.SetActive(peer);
    }

    /// <summary>
    ///     The next batch the presence loop would publish, stamped with
    ///     <paramref name="serverTimeMs" />, or null when there is nothing to send. Goes through the
    ///     loop's own per-batch turn, so the interval snapshot deadline and the metrics advance
    ///     exactly as they do in the deployed publisher.
    /// </summary>
    public ParcelChangesBatch? NextBatch(long serverTimeMs) =>
        NextBatchWithReason(serverTimeMs).Batch;

    /// <summary>
    ///     <see cref="NextBatch" /> plus the reason the batch is a snapshot, for the cadence tests.
    /// </summary>
    public (ParcelChangesBatch? Batch, PresenceSnapshotReason? Reason) NextBatchWithReason(long serverTimeMs)
    {
        Clock.UnixTimeMs = serverTimeMs;

        return Publisher.TryTakeNextParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? reason)
            ? (batch, reason)
            : (null, null);
    }

    /// <summary>
    ///     Empties the cluster feed's outbox, as a connected broker's drain loop does, and reports how
    ///     many messages it took.
    ///     <para />
    ///     Load-bearing for any test about presence eviction: the two feeds share
    ///     <c>Nats:ChannelCapacity</c> and the one <c>CountDropped</c> path, so an undelivered backlog
    ///     of cluster assignments evicts — and forces a presence snapshot — quite apart from anything
    ///     the presence outbox did. Draining it is what leaves the presence outbox as the only thing
    ///     that can evict.
    /// </summary>
    public int DrainClusterOutbox()
    {
        var drained = 0;

        while (Publisher.TryDequeueNext(out string _, out IMessage? message))
        {
            Publisher.Return(message);
            drained++;
        }

        return drained;
    }

    /// <summary>
    ///     Every batch one turn of the publish loop would put on the wire, in order — the loop drains
    ///     up to <see cref="NatsPublisher.MAX_PARCEL_BATCHES_PER_TURN" /> batches per tick, because a
    ///     snapshot travels behind the delta batch that was pending when it was collected (A2).
    ///     <para />
    ///     Each batch is cloned: the publisher owns one reused instance, so the caller would otherwise
    ///     be handed the same object twice.
    /// </summary>
    public List<ParcelChangesBatch> NextTurn(long serverTimeMs)
    {
        Clock.UnixTimeMs = serverTimeMs;

        var batches = new List<ParcelChangesBatch>();

        for (var i = 0; i < NatsPublisher.MAX_PARCEL_BATCHES_PER_TURN; i++)
        {
            if (!Publisher.TryTakeNextParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? _))
                break;

            batches.Add(batch.Clone());
        }

        return batches;
    }

    /// <summary>
    ///     <see cref="NextTurn" /> with each batch's snapshot reason alongside it, for the cadence
    ///     tests.
    /// </summary>
    public List<(ParcelChangesBatch Batch, PresenceSnapshotReason? Reason)> NextTurnWithReasons(long serverTimeMs)
    {
        Clock.UnixTimeMs = serverTimeMs;

        var batches = new List<(ParcelChangesBatch, PresenceSnapshotReason?)>();

        for (var i = 0; i < NatsPublisher.MAX_PARCEL_BATCHES_PER_TURN; i++)
        {
            if (!Publisher.TryTakeNextParcelBatch(out ParcelChangesBatch batch, out PresenceSnapshotReason? reason))
                break;

            batches.Add((batch.Clone(), reason));
        }

        return batches;
    }

    /// <summary>
    ///     The next batch as bytes on the wire: the publisher's own serializer writing into a buffer,
    ///     which is what <c>NatsConnection.PublishAsync</c> does with it. Null when there is nothing
    ///     to send.
    /// </summary>
    public byte[]? NextBatchBytes(long serverTimeMs)
    {
        ParcelChangesBatch? batch = NextBatch(serverTimeMs);

        return batch is null ? null : Serialize(batch);
    }

    public static byte[] Serialize(ParcelChangesBatch batch)
    {
        var buffer = new ArrayBufferWriter<byte>();

        NatsPublisher.SERIALIZER.Serialize(buffer, batch);

        return buffer.WrittenSpan.ToArray();
    }
}
