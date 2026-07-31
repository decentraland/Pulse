using Decentraland.Kernel.Comms.V3;
using Decentraland.Pulse;
using Google.Protobuf;
using NATS.Client.Core;
using System.Buffers;
using static PulseTestClient.ConnStringRedaction;

namespace PulseTestClient.Bridge;

/// <summary>
///     Stands in for comms-gatekeeper so the harness can close the loop without Postgres, LiveKit
///     credentials or a deny-list fetch. It translates Pulse's cluster feed into the message
///     ws-connector hands a client:
///     <code>
///     Pulse → peer.{addr}.cluster_change → [this] → engine.peer.{addr}.island_changed → ws-connector
///     </code>
///     <para />
///     The asymmetry in those two subjects is real and load-bearing, not a typo to tidy up: Pulse
///     publishes the unprefixed <c>peer.…</c> subject, while ws-connector subscribes to the
///     <c>engine.</c>-prefixed one. The real gatekeeper bridges exactly this gap.
///     <para />
///     Behaviour is copied from the real service rather than invented, so an assertion written
///     against this stub still holds once the real gatekeeper is in the loop. What is deliberately
///     dropped is everything that needs infrastructure: the ban and deny-list checks, and the
///     queue group that keeps replicas from each minting for the same event.
/// </summary>
public sealed class StubGatekeeper
{
    /// <summary>
    ///     What Pulse publishes. Wildcard on the wallet, and unprefixed because Pulse's own subject
    ///     prefix is empty in the harness.
    /// </summary>
    private const string CLUSTER_CHANGE_SUBJECT = "peer.*.cluster_change";

    // Required, not cosmetic: the room name is `island-{clusterId}`, and downstream code recovers
    // the cluster by stripping this prefix. Emitting a bare cluster id would parse as a different
    // kind of room entirely.
    private const string ISLAND_ROOM_PREFIX = "island-";

    private static readonly INatsSerialize<IMessage> SERIALIZER = new ProtobufSerializer();
    private static readonly INatsDeserialize<byte[]> RAW = new RawDeserializer();

    private readonly IConnStringSource connStrings;

    // Guards the dictionary only. Every wallet's own state is touched by that wallet's chain alone,
    // and consecutive chain steps are ordered by an await, so the state objects need no lock.
    private readonly Lock walletsLock = new ();
    private readonly Dictionary<string, WalletState> wallets = new (StringComparer.Ordinal);

    private StubGatekeeper(IConnStringSource connStrings) =>
        this.connStrings = connStrings;

    /// <summary>
    ///     Runs the bridge until <paramref name="ct" /> is cancelled. Returns immediately, having
    ///     said why, when the mode is <c>off</c> or no broker is configured.
    /// </summary>
    public static async Task RunAsync(ClientOptions options, CancellationToken ct)
    {
        string mode = options.BridgeMode.Trim().ToLowerInvariant();

        if (mode == "off")
        {
            Console.WriteLine("[bridge] off — not subscribing; a real comms-gatekeeper is expected on the broker");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.NatsUrl))
        {
            Console.WriteLine("[bridge] off — --nats-url is empty, so there is no broker to bridge");
            return;
        }

        IConnStringSource connStrings = CreateConnStrings(mode);

        // These three lines, always. Silent no-delivery is how this system fails — a subject that
        // does not match, or a broker that is not the one Pulse publishes to, looks identical to
        // "nothing happened" from every other vantage point.
        Console.WriteLine($"[bridge] subject: {CLUSTER_CHANGE_SUBJECT}");
        Console.WriteLine($"[bridge] broker: {SanitizeBrokerUrl(options.NatsUrl)}");
        Console.WriteLine($"[bridge] mode: {mode} — {connStrings.Description}");

        await new StubGatekeeper(connStrings).SubscribeAsync(options.NatsUrl, ct);
    }

    private static IConnStringSource CreateConnStrings(string mode) =>
        mode switch
        {
            "synthetic" => new SyntheticConnStringSource(),
            "livekit" => LiveKitConnStringSource.FromEnvironment(),
            _ => throw new PulseException(
                $"Unknown --bridge-mode '{mode}'; expected one of synthetic, livekit, off"),
        };

    private async Task SubscribeAsync(string natsUrl, CancellationToken ct)
    {
        var opts = NatsOpts.Default with { Url = natsUrl, Name = "pulse-stub-gatekeeper" };

        await using var connection = new NatsConnection(opts);

        try
        {
            // No queue group, unlike the real service: this stub is meant to be the only subscriber.
            // Running it alongside a real gatekeeper would give each client two island_changed
            // messages carrying two different tokens, which is what --bridge-mode=off avoids.
            await foreach (NatsMsg<byte[]> msg in connection.SubscribeAsync(
                               CLUSTER_CHANGE_SUBJECT, serializer: RAW, cancellationToken: ct))
                Handle(msg, connection, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }

        Console.WriteLine("[bridge] stopped");
    }

    /// <summary>
    ///     Kept synchronous and guarded whole. An exception escaping here unwinds into the NATS
    ///     client's reader loop and stops delivery on every subject, so one malformed message would
    ///     take the entire bridge down rather than itself.
    /// </summary>
    private void Handle(NatsMsg<byte[]> msg, NatsConnection connection, CancellationToken ct)
    {
        var wallet = string.Empty;

        try
        {
            // The wallet is the token after `peer.`. Lower-cased because that is the only casing
            // ws-connector subscribes on: a checksummed address here would publish to a subject
            // nobody is listening to, and silently deliver nothing.
            string[] parts = msg.Subject.Split('.');
            wallet = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

            if (wallet.Length == 0)
            {
                Console.WriteLine($"[bridge] cannot extract a wallet from subject {msg.Subject}");
                return;
            }

            if (msg.Data is not { } data)
            {
                Console.WriteLine($"[bridge] cluster_change for {wallet} carried no payload");
                return;
            }

            PeerClusterChange change = PeerClusterChange.Parser.ParseFrom(data);

            // Protobuf decodes a missing cluster_id as "", and unguarded that would put every such
            // peer into one shared `island-` room together.
            if (change.ClusterId.Length == 0)
            {
                Console.WriteLine($"[bridge] cannot process cluster_change for {wallet}: empty clusterId");
                return;
            }

            Enqueue(wallet, change.ClusterId, connection, ct);
        }
        catch (Exception e)
        {
            Console.WriteLine(
                $"[bridge] cannot process cluster_change on {msg.Subject}: {Redact(e.Message)}");
        }
    }

    /// <summary>
    ///     Chains this event behind whatever is already in flight for the same wallet. Two events
    ///     for one wallet must publish in order — reversed, the second would announce a room the
    ///     first has already superseded and leave the next <c>FromIslandId</c> pointing at it.
    ///     Chained per wallet, so one slow mint delays that wallet alone.
    /// </summary>
    private void Enqueue(string wallet, string clusterId, NatsConnection connection, CancellationToken ct)
    {
        WalletState state;

        lock (walletsLock)
        {
            if (!wallets.TryGetValue(wallet, out WalletState? existing))
                wallets[wallet] = existing = new WalletState();

            state = existing;
        }

        // Read-modify-write without a lock: only the subscribe loop reaches this, and it is a single
        // reader. The chain itself is what orders the work, not this assignment.
        Task previous = state.Tail;
        state.Tail = ChainAsync(previous, state, wallet, clusterId, connection, ct);
    }

    private async Task ChainAsync(
        Task previous,
        WalletState state,
        string wallet,
        string clusterId,
        NatsConnection connection,
        CancellationToken ct)
    {
        // Safe to await bare because PublishAsync below swallows everything: the tail never faults,
        // so an event that failed cannot strand the events queued behind it.
        await previous.ConfigureAwait(false);
        await PublishAsync(state, wallet, clusterId, connection, ct).ConfigureAwait(false);
    }

    private async Task PublishAsync(
        WalletState state,
        string wallet,
        string clusterId,
        NatsConnection connection,
        CancellationToken ct)
    {
        var subject = $"engine.peer.{wallet}.island_changed";

        try
        {
            // A room name, never the bare cluster id. The real gatekeeper can also shard a room when
            // a cluster outgrows one, but that needs the engine.islands topology snapshot this stub
            // does not consume — and with a cluster's size unknown, the real one emits this same
            // unsharded name, so what is asserted here is what it would send.
            string room = ISLAND_ROOM_PREFIX + clusterId;

            var message = new IslandChangedMessage
            {
                IslandId = room,
                ConnStr = connStrings.Build(wallet, room, clusterId),

                // Peers is left empty on purpose, as the real gatekeeper leaves it: unity-explorer
                // reads ConnStr and nothing else off this message.
            };

            // Set only when a previous room exists. Assigning "" instead would put the field on the
            // wire, and a first assignment has to be distinguishable from a move out of nowhere.
            if (state.Room is { } previous)
                message.FromIslandId = previous;

            // No suppression when the room is unchanged: Pulse only re-announces a cluster after
            // forgetting a peer, which is a reconnect that needs a fresh token.
            await connection.PublishAsync(subject, message, serializer: SERIALIZER, cancellationToken: ct)
                            .ConfigureAwait(false);

            // Advanced only after the publish landed, as the real gatekeeper does. A failed publish
            // that still moved this on would have the next event claim a move the client never saw.
            state.Room = room;

            Console.WriteLine($"[bridge] {wallet} -> {room} on {subject} ({Redact(message.ConnStr)})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception e)
        {
            // Swallowed deliberately, and this is the reason ChainAsync can await the tail bare.
            Console.WriteLine($"[bridge] failed to publish on {subject}: {Redact(e.Message)}");
        }
    }

    /// <summary>
    ///     Reduces a broker address to host and port before it is logged. A NATS URL may be a
    ///     comma-separated seed list and may carry userinfo (<c>nats://user:password@host:4222</c>),
    ///     so every entry is reduced and the results rejoined.
    /// </summary>
    private static string SanitizeBrokerUrl(string url)
    {
        const string UNPARSED = "(unparsed broker url)";

        string joined = string.Join(", ", url
            .Split(',')
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .Select(entry =>
                Uri.TryCreate(entry, UriKind.Absolute, out Uri? parsed) && parsed.Host.Length > 0
                    ? parsed.IsDefaultPort ? parsed.Host : $"{parsed.Host}:{parsed.Port}"
                    : UNPARSED));

        return joined.Length == 0 ? UNPARSED : joined;
    }

    /// <summary>One wallet's publish ordering and the previous room <c>FromIslandId</c> needs.</summary>
    private sealed class WalletState
    {
        /// <summary>Last chained publish for this wallet. Never faults, by construction.</summary>
        public Task Tail = Task.CompletedTask;

        /// <summary>
        ///     Room last published for this wallet, null until one lands. Null is what makes
        ///     <c>FromIslandId</c> absent rather than empty on a peer's first assignment.
        /// </summary>
        public string? Room;
    }

    /// <summary>
    ///     Writes a protobuf message straight into the buffer the NATS client supplies, so a publish
    ///     neither rents a buffer of its own nor copies the encoded bytes into one.
    /// </summary>
    private sealed class ProtobufSerializer : INatsSerialize<IMessage>
    {
        public void Serialize(IBufferWriter<byte> bufferWriter, IMessage value) =>
            value.WriteTo(bufferWriter);
    }

    /// <summary>
    ///     Copies the payload out instead of decoding it. Decoding here would run inside the
    ///     client's reader loop, where a malformed message would throw somewhere nothing can catch
    ///     it; <see cref="Handle" /> decodes it under a guard instead.
    /// </summary>
    private sealed class RawDeserializer : INatsDeserialize<byte[]>
    {
        public byte[] Deserialize(in ReadOnlySequence<byte> buffer) =>
            buffer.ToArray();
    }
}
