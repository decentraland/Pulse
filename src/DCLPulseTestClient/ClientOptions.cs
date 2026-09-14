namespace PulseTestClient;

public class ClientOptions
{
    public string AccountPrefix { get; init; } = "enetclient-test";
    public string ServerIp { get; init; } = "127.0.0.1";
    public int ServerPort { get; init; } = 7777;
    public string Realm { get; init; } = "main";
    public float RotateSpeed { get; init; } = 90f;
    public int BotCount { get; init; } = 1;
    public float PositionX { get; init; } = -104f;
    public float PositionY { get; init; }
    public float PositionZ { get; init; } = 5f;
    public float SpawnRadius { get; init; } = 10f;
    public float DispersionRadius { get; init; } = 20f;
    public int BotOffset { get; init; }
    public int TotalBotCount { get; init; }
    public string SceneListenerParcels { get; init; } = "";

    /// <summary>Transport to use: <c>enet</c> (default) or <c>webtransport</c>.</summary>
    public string Transport { get; init; } = "enet";

    /// <summary>
    ///     Whether each bot also opens a ws-connector session on its own wallet to observe the LiveKit
    ///     conn string. Off by default so existing runs are unchanged.
    /// </summary>
    public bool CommsEnabled { get; init; }

    /// <summary>ws-connector WebSocket endpoint.</summary>
    public string CommsUrl { get; init; } = "ws://127.0.0.1:5000/ws";

    /// <summary>
    ///     Deadline for a conn string to arrive after a bot connects. Default covers three
    ///     <c>DwellPasses</c> at 1 Hz plus slack.
    /// </summary>
    public int ExpectConnStringWithinSeconds { get; init; } = 15;

    /// <summary>
    ///     Whether each conn string received is also used to actually join its LiveKit room. Off by
    ///     default: it opens a real WebRTC session per bot, which existing runs neither need nor expect.
    /// </summary>
    public bool JoinLiveKit { get; init; }

    /// <summary>
    ///     Device label forwarded to MetaForge's <c>--device</c> flag, or null to use the account's
    ///     default identity. Lets two separate processes run different ephemeral identities on the same
    ///     account/wallet — e.g. a takeover scenario with two "devices" sharing one wallet.
    /// </summary>
    public string? Device { get; init; }

    /// <summary>
    ///     Delay, in milliseconds, between the Pulse handshake completing and this bot starting its
    ///     ws-connector handshake. The Pulse session, its snapshots and its movement all run normally in
    ///     the meantime — only the comms channel's start is deferred. Forces the takeover race's "Order
    ///     2": Pulse's cluster tracker can publish a takeover assignment before this bot's ws-connector
    ///     socket exists to receive it, so the direct <c>island_changed</c> delivery is dropped and only
    ///     gatekeeper's connect re-announce can recover it. 0 (default) starts the handshake immediately.
    /// </summary>
    public int CommsDelayMs { get; init; }

    /// <summary>
    ///     Debug-only mode: joins ONLY a LiveKit room using this conn string — no account resolution, no
    ///     Pulse session, no ws-connector handshake. Models a "ghost" participant: a displaced device
    ///     that outlived its own eviction, connected with a harness-minted token the wallet's real
    ///     session never asked for. When set, every other flag that drives the Pulse/comms flow is
    ///     ignored and this is the whole process.
    /// </summary>
    public string? JoinConnStr { get; init; }

    /// <summary>
    ///     Debug-only: only meaningful together with <see cref="JoinLiveKit" />. After this bot's LiveKit
    ///     room disconnects it, re-attempts the join after this many milliseconds using the SAME
    ///     connection string (and therefore the same, possibly since-revoked, token) it first joined
    ///     with — mirroring the Unity island room's backoff reconnect against a stale conn string. 0
    ///     (default) never re-joins.
    /// </summary>
    public int RejoinAfterMs { get; init; }

    public static ClientOptions FromArgs(string[] args)
    {
        string Arg(string name, string fallback) =>
            args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..] ?? fallback;

        // Accepts both the bare `--flag` and the `--flag=value` form the other options use.
        bool Flag(string name) =>
            args.Any(a => a == $"--{name}") || bool.TryParse(Arg(name, "false"), out bool v) && v;

        return new ClientOptions
        {
            AccountPrefix = Arg("account", "enetclient-test"),
            ServerIp = Arg("ip", "127.0.0.1"),
            ServerPort = int.Parse(Arg("port", "7777")),
            Realm = Arg("realm", "main"),
            RotateSpeed = float.Parse(Arg("rotate-speed", "90")),
            BotCount = int.Parse(Arg("bot-count", "1")),
            PositionX = float.Parse(Arg("pos-x", "-104")),
            PositionY = float.Parse(Arg("pos-y", "0")),
            PositionZ = float.Parse(Arg("pos-z", "5")),
            SpawnRadius = float.Parse(Arg("spawn-radius", "10")),
            DispersionRadius = float.Parse(Arg("dispersion-radius", "20")),
            BotOffset = int.Parse(Arg("bot-offset", "0")),
            TotalBotCount = int.Parse(Arg("total-bot-count", "0")),
            SceneListenerParcels = Arg("scene-listener-parcels", ""),
            Transport = Arg("transport", "enet"),
            CommsEnabled = Flag("comms-enabled"),
            CommsUrl = Arg("comms-url", "ws://127.0.0.1:5000/ws"),
            ExpectConnStringWithinSeconds = int.Parse(Arg("expect-conn-string-within", "15")),
            JoinLiveKit = Flag("join-livekit"),
            Device = Arg("device", "") is {Length: > 0} device ? device : null,
            CommsDelayMs = int.Parse(Arg("comms-delay-ms", "0")),
            JoinConnStr = Arg("join-conn-str", "") is {Length: > 0} joinConnStr ? joinConnStr : null,
            RejoinAfterMs = int.Parse(Arg("rejoin-after-ms", "0")),
        };
    }
}
