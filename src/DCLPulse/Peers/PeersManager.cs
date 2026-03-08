using DCL.Auth;
using System.Diagnostics;
using System.Threading.Channels;
using Decentraland.Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Text.Json;
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
    private readonly PeerStateFactory peerStateFactory;
    private readonly PlayerStateInputHandler inputHandler;
    private readonly IAreaOfInterest areaOfInterest;
    private readonly SnapshotBoard snapshotBoard;
    private readonly PeerOptions peerOptions;

    private readonly int workerCount;

    // One channel per worker. Router is the sole writer (SingleWriter=true).
    private readonly Channel<IncomingMessage>[] workerChannels;

    // Per-worker peer state — indexed by [workerIndex][peerIndex].
    // Accessed only by the owning worker, so no concurrent access.
    private readonly Dictionary<PeerIndex, PeerState>[] peerStates;
    private readonly AuthChainValidator authChainValidator;

    public PeersManager(
        MessagePipe messagePipe,
        PeerStateFactory peerStateFactory,
        PlayerStateInputHandler inputHandler,
        IAreaOfInterest areaOfInterest,
        SnapshotBoard snapshotBoard,
        PeerOptions peerOptions,
        ILogger<PeersManager> logger)
    {
        this.messagePipe = messagePipe;
        this.logger = logger;
        this.inputHandler = inputHandler;
        this.peerStateFactory = peerStateFactory;
        this.areaOfInterest = areaOfInterest;
        this.snapshotBoard = snapshotBoard;
        this.peerOptions = peerOptions;
        workerCount = Environment.ProcessorCount;
        authChainValidator = new AuthChainValidator(new NethereumPersonalSignVerifier());

        workerChannels = new Channel<IncomingMessage>[workerCount];
        peerStates = new Dictionary<PeerIndex, PeerState>[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            workerChannels[i] = Channel.CreateUnbounded<IncomingMessage>(
                new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

            peerStates[i] = new Dictionary<PeerIndex, PeerState>();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new Task[workerCount + 1];

        tasks[0] = RouteAsync(stoppingToken);

        for (var i = 0; i < workerCount; i++)
            tasks[i + 1] = WorkerAsync(i, workerChannels[i].Reader, stoppingToken);

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
                workerChannels[index].Writer.TryWrite(msg);
            }
        }
        finally
        {
            // Signal every worker to drain and exit — runs even on cancellation.
            foreach (Channel<IncomingMessage> ch in workerChannels)
                ch.Writer.TryComplete();
        }
    }

    /// <summary>
    ///     Drain-then-simulate worker loop.
    ///     1. Drain all available messages (non-blocking).
    ///     2. If next tick isn't due, wait for messages with timeout.
    ///     3. When tick fires, run simulation for all owned observers.
    /// </summary>
    private async Task WorkerAsync(int workerIndex, ChannelReader<IncomingMessage> reader, CancellationToken ct)
    {
        Dictionary<PeerIndex, PeerState> peers = peerStates[workerIndex];

        var simulation = new PeerSimulation(
            areaOfInterest,
            snapshotBoard,
            messagePipe,
            peerOptions.SimulationSteps);

        uint tickCounter = 0;
        long nextTickTime = Stopwatch.GetTimestamp() + TickMsToStopwatchTicks(simulation.BaseTickMs);

        // Reusable CTS for tick-deadline waits — avoids allocating a new linked CTS every 50ms.
        // TryReset (available since .NET 6) restores the CTS to a non-cancelled state.
        using var tickCts = new CancellationTokenSource();
        using CancellationTokenRegistration reg = ct.Register(static s => ((CancellationTokenSource)s!).Cancel(), tickCts);

        while (!ct.IsCancellationRequested)
        {
            DrainMessages(reader, peers, workerIndex);

            long now = Stopwatch.GetTimestamp();

            if (now >= nextTickTime) { nextTickTime = RunSimulationTick(simulation, peers, ref tickCounter, now, workerIndex); }

            if (!await WaitForMessagesOrTick(reader, tickCts, nextTickTime, ct))
                break;
        }

        DrainMessages(reader, peers, workerIndex);
    }

    private void DrainMessages(ChannelReader<IncomingMessage> reader, Dictionary<PeerIndex, PeerState> peers, int workerIndex)
    {
        while (reader.TryRead(out IncomingMessage item))
        {
            try { HandleMessage(peers, item.From, item.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling message from peer {PeerIndex} on worker {Worker}.",
                    item.From.Value, workerIndex);
            }
        }
    }

    private long RunSimulationTick(
        PeerSimulation simulation,
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
        long nextTickTime = now + TickMsToStopwatchTicks(simulation.BaseTickMs);
        return nextTickTime;
    }

    /// <summary>
    ///     Suspends the worker until a message arrives or the next tick deadline,
    ///     whichever comes first. Returns false if the parent cancellation token fired.
    /// </summary>
    private static async Task<bool> WaitForMessagesOrTick(
        ChannelReader<IncomingMessage> reader,
        CancellationTokenSource tickCts,
        long nextTickTime,
        CancellationToken ct)
    {
        long remaining = nextTickTime - Stopwatch.GetTimestamp();

        if (remaining <= 0)
            return true;

        var waitMs = (int)(remaining * 1000 / Stopwatch.Frequency);

        if (waitMs <= 0)
            return true;

        tickCts.CancelAfter(waitMs);

        try { await reader.WaitToReadAsync(tickCts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timer expired — loop back to drain + simulate
        }

        return tickCts.TryReset();
    }

    private static long TickMsToStopwatchTicks(uint ms) =>
        ms * Stopwatch.Frequency / 1000;

    /// <summary>
    ///     Executed on the thread pool
    /// </summary>
    private void HandleMessage(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        // TODO Handle the state machine based on the peer state

        switch (message.MessageCase)
        {
            // TODO separate every handler to the respective class
            case ClientMessage.MessageOneofCase.Handshake:
                HandleHandshake(peers, from, message.Handshake);
                break;
            case ClientMessage.MessageOneofCase.Input:
                // At this point peers may not contain a peer state at all - it should be divised from the package

                if (!peers.TryGetValue(from, out PeerState? state) || state.ConnectionState != PeerConnectionState.AUTHENTICATED)

                    // Skip messages from unauthenticated peer
                    // TODO add analytics to understand if there is a problem
                    return;

                // Input Handler doesn't produce output messages as the simulation is running completely independently in its own loop for each peer
                // and produces diffs based on the interest management
                inputHandler.Handle(from, state, message.Input);
                break;
        }
    }

    private void HandleHandshake(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, HandshakeRequest handshakeRequest)
    {
        string authChainJson = handshakeRequest.AuthChain.ToStringUtf8();
        Dictionary<string, string>? headers = JsonSerializer.Deserialize<Dictionary<string, string>>(authChainJson);

        if (headers == null)
        {
            messagePipe.Send(new OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = false,
                    Error = "Invalid auth chain JSON",
                },
            }, ITransport.PacketMode.RELIABLE));

            return;
        }

        try
        {
            IReadOnlyList<AuthLink> chain = AuthChainParser.ParseFromSignedFetchHeaders(headers);

            string timestamp = string.Empty;
            string metadata = string.Empty;

            foreach (KeyValuePair<string, string> kv in headers)
            {
                if (kv.Key.Equals("x-identity-timestamp", StringComparison.OrdinalIgnoreCase))
                    timestamp = kv.Value;

                if (kv.Key.Equals("x-identity-metadata", StringComparison.OrdinalIgnoreCase))
                    metadata = kv.Value;
            }

            string expectedPayload = SignedFetch.BuildSignedFetchPayload("connect", "/", timestamp, metadata);
            AuthChainValidationResult result = authChainValidator.Validate(chain, expectedPayload);

            PeerState peer = peerStateFactory.Create();
            peer.WalletId = result.UserAddress;
            peer.ConnectionState = PeerConnectionState.AUTHENTICATED;

            peers[from] = peer;
            snapshotBoard.SetActive(from);

            messagePipe.Send(new OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = true,
                },
            }, ITransport.PacketMode.RELIABLE));
        }
        catch (Exception e)
        {
            messagePipe.Send(new OutgoingMessage(from, new ServerMessage
            {
                Handshake = new HandshakeResponse
                {
                    Success = false,
                    Error = e.Message,
                },
            }, ITransport.PacketMode.RELIABLE));
        }
    }
}
