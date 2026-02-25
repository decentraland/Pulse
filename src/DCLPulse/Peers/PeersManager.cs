using System.Threading.Channels;
using Decentraland.Pulse;
using Pulse.Messaging;
using static Pulse.Messaging.MessagePipe;

namespace Pulse.Peers;

/// <summary>
///     Owns all peer state and processes inbound <see cref="ClientMessage" /> envelopes.
///     Threading model
///     ───────────────
///     Router task  — reads the flat stream from <see cref="MessagePipe.ReadMessagesAsync" />
///     and fans out to channel[PeerId % WorkerCount].
///     Worker[i]    — owns a fixed stripe of peers (those where PeerId % WorkerCount == i).
///     Has exclusive access to its peer states — no locking needed.
///     Drains its channel until completion, then exits.
///     Per-peer ordering guarantee
///     ───────────────────────────
///     Every message from a given peer lands on the same worker channel, so messages
///     are processed in arrival order per peer (ch0 handshake before auth checks, etc.).
/// </summary>
public sealed class PeersManager : BackgroundService
{
    private readonly MessagePipe messagePipe;
    private readonly ILogger<PeersManager> logger;
    private readonly int workerCount;

    // One channel per worker. Router is the sole writer (SingleWriter=true).
    private readonly Channel<IncomingMessage>[] workerChannels;

    // Per-worker peer state — indexed by [workerIndex][peerId].
    // Accessed only by the owning worker, so no concurrent access.
    private readonly Dictionary<PeerId, PeerState>[] peerStates;

    public PeersManager(MessagePipe messagePipe, ILogger<PeersManager> logger)
    {
        this.messagePipe = messagePipe;
        this.logger = logger;
        workerCount = Environment.ProcessorCount;

        workerChannels = new Channel<IncomingMessage>[workerCount];
        peerStates = new Dictionary<PeerId, PeerState>[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            workerChannels[i] = Channel.CreateUnbounded<IncomingMessage>(
                new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

            peerStates[i] = new Dictionary<PeerId, PeerState>();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new Task[workerCount + 1];

        tasks[0] = RouteAsync(stoppingToken);

        for (var i = 0; i < workerCount; i++)
            tasks[i + 1] = WorkerAsync(i, workerChannels[i].Reader);

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
            await foreach (IncomingMessage msg in messagePipe.ReadMessagesAsync(ct))
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
    ///     Processes messages for peers assigned to this worker stripe.
    ///     Does not take a CancellationToken — relies solely on channel completion for clean shutdown,
    ///     so any messages already queued are processed before the worker exits.
    /// </summary>
    private async Task WorkerAsync(int workerIndex, ChannelReader<IncomingMessage> reader)
    {
        Dictionary<PeerId, PeerState> peers = peerStates[workerIndex];

        await foreach (IncomingMessage item in reader.ReadAllAsync())
        {
            try { HandleMessage(peers, item.From, item.Message); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error handling message from peer {PeerId} on worker {Worker}.",
                    item.From.Value, workerIndex);
            }
        }
    }

    /// <summary>
    ///     Executed on the thread pool
    /// </summary>
    private void HandleMessage(Dictionary<PeerId, PeerState> peers, PeerId from, ClientMessage message)
    {
        // TODO Handle the state machine based on the peer state
        // At this point peers may not contain a peer state at all - it should be divised from the package

        switch (message.MessageCase)
        {
            case ClientMessage.MessageOneofCase.Handshake:
                break;
        }
    }
}
