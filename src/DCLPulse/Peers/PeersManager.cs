using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Threading.Channels;
using static Pulse.Messaging.MessagePipe;

namespace Pulse.Peers;

/// <summary>
///     Owns all peer state and processes inbound <see cref="ClientMessage" /> envelopes.
///     Threading model
///     ───────────────
///     Router task  — reads the flat stream from <see cref="MessagePipe.ReadIncomingMessagesAsync" />
///     and fans out to channel[PeerIndex % WorkerCount].
///     Worker[i]    — owns a fixed stripe of peers (those where PeerIndex % WorkerCount == i).
///     Has exclusive access to its peer states — no locking needed.
///     Drains messages, then runs simulation at fixed tick intervals.
///     Per-peer ordering guarantee
///     ───────────────────────────
///     Every message from a given peer lands on the same worker channel, so messages
///     are processed in arrival order per peer (ch0 handshake before auth checks, etc.).
/// </summary>
public sealed class PeersManager : BackgroundService
{
    private readonly MessagePipe messagePipe;
    private readonly ILogger<PeersManager> logger;
    private readonly ILogger<PeerSimulation> peerSimulationLogger;
    private readonly ITimeProvider timeProvider;
    private readonly PeerStateFactory peerStateFactory;
    private readonly IAreaOfInterest areaOfInterest;
    private readonly SnapshotBoard snapshotBoard;
    private readonly SpatialGrid spatialGrid;
    private readonly IdentityBoard identityBoard;
    private readonly PeerOptions peerOptions;
    private readonly int workerCount;

    // One channel per worker. Router is the sole writer (SingleWriter=true).
    private readonly Channel<IncomingMessage>[] messageChannels;
    private readonly Channel<PeerLifeCycleEvent>[] peerLifeCycleChannels;

    // Per-worker peer state — indexed by [workerIndex][peerIndex].
    // Accessed only by the owning worker, so no concurrent access.
    internal readonly Dictionary<PeerIndex, PeerState>[] peerStates;
    private readonly Dictionary<ClientMessage.MessageOneofCase, IMessageHandler> messageHandlers;
    private readonly ITransport transport;
    private readonly ProfileBoard profileBoard;
    private readonly EmoteBoard emoteBoard;
    private readonly TeleportBoard teleportBoard;

    public PeersManager(
        MessagePipe messagePipe,
        PeerStateFactory peerStateFactory,
        IAreaOfInterest areaOfInterest,
        SnapshotBoard snapshotBoard,
        SpatialGrid spatialGrid,
        IdentityBoard identityBoard,
        PeerOptions peerOptions,
        ILogger<PeersManager> logger,
        ILogger<PeerSimulation> peerSimulationLogger,
        ITimeProvider timeProvider,
        Dictionary<ClientMessage.MessageOneofCase, IMessageHandler> messageHandlers,
        ITransport transport,
        ProfileBoard profileBoard,
        EmoteBoard emoteBoard,
        TeleportBoard teleportBoard)
    {
        this.messagePipe = messagePipe;
        this.logger = logger;
        this.peerSimulationLogger = peerSimulationLogger;
        this.timeProvider = timeProvider;
        this.messageHandlers = messageHandlers;
        this.transport = transport;
        this.profileBoard = profileBoard;
        this.emoteBoard = emoteBoard;
        this.teleportBoard = teleportBoard;
        this.peerStateFactory = peerStateFactory;
        this.areaOfInterest = areaOfInterest;
        this.snapshotBoard = snapshotBoard;
        this.spatialGrid = spatialGrid;
        this.identityBoard = identityBoard;
        this.peerOptions = peerOptions;
        workerCount = Environment.ProcessorCount;

        messageChannels = new Channel<IncomingMessage>[workerCount];
        peerLifeCycleChannels = new Channel<PeerLifeCycleEvent>[workerCount];
        peerStates = new Dictionary<PeerIndex, PeerState>[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            messageChannels[i] = Channel.CreateUnbounded<IncomingMessage>(
                new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

            peerLifeCycleChannels[i] = Channel.CreateUnbounded<PeerLifeCycleEvent>(
                new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

            peerStates[i] = new Dictionary<PeerIndex, PeerState>();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new Task[workerCount + 1];

        tasks[0] = Task.WhenAll(RouteAsync(stoppingToken), RoutePeerLifeCycleEventsAsync(stoppingToken));

        for (var i = 0; i < workerCount; i++)
        {
            var simulation = new PeerSimulation(
                areaOfInterest, snapshotBoard, spatialGrid, identityBoard,
                messagePipe, peerOptions.SimulationSteps, timeProvider, transport, profileBoard, emoteBoard, teleportBoard, peerSimulationLogger,
                peerOptions.SelfMirrorEnabled, peerOptions.SelfMirrorTier);

            tasks[i + 1] = WorkerAsync(i, messageChannels[i].Reader,
                peerLifeCycleChannels[i].Reader, simulation, stoppingToken);
        }

        return Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Reads the flat message stream and fans out to per-peer-stripe worker channels.
    ///     Completes all worker channels (causing workers to drain and exit) when done.
    /// </summary>
    private async Task RouteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (IncomingMessage msg in messagePipe.ReadIncomingMessagesAsync(ct))
            {
                var index = (int)(msg.From.Value % (uint)workerCount);
                messageChannels[index].Writer.TryWrite(msg);
            }
        }
        finally
        {
            // Signal every worker to drain and exit — runs even on cancellation.
            foreach (Channel<IncomingMessage> ch in messageChannels)
                ch.Writer.TryComplete();
        }
    }

    private async Task RoutePeerLifeCycleEventsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (PeerLifeCycleEvent evt in messagePipe.ReadPeerLifeCycleAsync(ct))
            {
                var workerIndex = (int)(evt.From.Value % (uint)workerCount);
                peerLifeCycleChannels[workerIndex].Writer.TryWrite(evt);
            }
        }
        finally
        {
            // Signal every worker to drain and exit — runs even on cancellation.
            foreach (Channel<IncomingMessage> ch in messageChannels)
                ch.Writer.TryComplete();
        }
    }

    /// <summary>
    ///     Drain-then-simulate worker loop.
    ///     1. Drain all available messages (non-blocking).
    ///     2. If next tick isn't due, wait for messages with timeout.
    ///     3. When tick fires, run simulation for all owned observers.
    /// </summary>
    internal async Task WorkerAsync(int workerIndex,
        ChannelReader<IncomingMessage> messageReader,
        ChannelReader<PeerLifeCycleEvent> peerLifeCycleReader,
        IPeerSimulation simulation,
        CancellationToken ct)
    {
        Dictionary<PeerIndex, PeerState> peers = peerStates[workerIndex];

        uint tickCounter = 0;
        long nextTickTime = timeProvider.MonotonicTime + simulation.BaseTickMs;

        while (!ct.IsCancellationRequested)
        {
            DrainPeerLifeCycleEvents(peerLifeCycleReader, peers, workerIndex);
            DrainMessages(messageReader, peers, workerIndex);

            long now = timeProvider.MonotonicTime;

            if (now >= nextTickTime)
                nextTickTime = RunSimulationTick(simulation, peers, ref tickCounter, now, workerIndex);

            await WaitForMessagesOrTick(messageReader, nextTickTime, timeProvider, ct);
        }

        DrainPeerLifeCycleEvents(peerLifeCycleReader, peers, workerIndex);
        DrainMessages(messageReader, peers, workerIndex);
    }

    internal void DrainPeerLifeCycleEvents(ChannelReader<PeerLifeCycleEvent> reader, Dictionary<PeerIndex, PeerState> peers, int workerIndex)
    {
        while (reader.TryRead(out PeerLifeCycleEvent evt))
        {
            PeerIndex from = evt.From;

            try
            {
                if (evt.Type == PeerEventType.Connected)
                {
                    PeerState peerState = peerStateFactory.Create();
                    peerState.ConnectionState = PeerConnectionState.PENDING_AUTH;
                    peerState.TransportState = peerState.TransportState with { ConnectionTime = timeProvider.MonotonicTime };
                    peers[from] = peerState;

                    logger.LogInformation("Peer connected {Peer}", from);
                }
                else if (evt.Type == PeerEventType.Disconnected)
                {
                    if (!peers.TryGetValue(from, out PeerState? peerState))
                        peerState = peerStateFactory.Create();

                    peerState.ConnectionState = PeerConnectionState.DISCONNECTING;

                    peerState.TransportState = peerState.TransportState with
                    {
                        DisconnectionTime = timeProvider.MonotonicTime,
                    };

                    peers[from] = peerState;

                    logger.LogInformation("Peer disconnected {Peer}", from);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling {LifeCycleType} from peer {PeerIndex} on worker {Worker}.",
                    evt.Type, from.Value, workerIndex);
            }
        }
    }

    private void DrainMessages(ChannelReader<IncomingMessage> reader, Dictionary<PeerIndex, PeerState> peers, int workerIndex)
    {
        while (reader.TryRead(out IncomingMessage item))
        {
            try
            {
                if (messageHandlers.TryGetValue(item.Message.MessageCase, out var handler))
                    handler.Handle(peers, item.From, item.Message);
                else
                    logger.LogWarning("No handler found for message {MessageCase}, skipped processing", item.Message.MessageCase);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling message from peer {PeerIndex} on worker {Worker}.",
                    item.From.Value, workerIndex);
            }
        }
    }

    private long RunSimulationTick(
        IPeerSimulation simulation,
        Dictionary<PeerIndex, PeerState> peers,
        ref uint tickCounter,
        long now,
        int workerIndex)
    {
        try { simulation.SimulateTick(peers, tickCounter); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in simulation tick {Tick} on worker {Worker}.",
                tickCounter, workerIndex);
        }

        tickCounter++;
        long nextTickTime = now + simulation.BaseTickMs;
        return nextTickTime;
    }

    /// <summary>
    ///     Suspends the worker until a message arrives or the next tick deadline,
    ///     whichever comes first
    /// </summary>
    internal static async Task WaitForMessagesOrTick(
        ChannelReader<IncomingMessage> reader,
        long nextTickTime,
        ITimeProvider timeProvider,
        CancellationToken ct)
    {
        long remaining = nextTickTime - timeProvider.MonotonicTime;

        if (remaining <= 0)
            return;

        var waitMs = (int)remaining;

        Task<bool> waitToReadTask = reader.WaitToReadAsync(ct).AsTask();
        Task delayTask = Task.Delay(waitMs, ct);
        await Task.WhenAny(waitToReadTask, delayTask);
    }
}
