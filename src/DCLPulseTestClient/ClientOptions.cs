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

    /// <summary>Transport to use: <c>enet</c> (default) or <c>webtransport</c>.</summary>
    public string Transport { get; init; } = "enet";

    /// <summary>
    ///     Entry point to run: <c>bots</c> (default) drives the simulation, <c>bridge</c> runs the stub
    ///     gatekeeper alone.
    /// </summary>
    public string Mode { get; init; } = "bots";

    /// <summary>
    ///     Whether each bot also opens a ws-connector session on its own wallet to observe the LiveKit
    ///     conn string. Off by default so existing runs are unchanged.
    /// </summary>
    public bool CommsEnabled { get; init; }

    /// <summary>ws-connector WebSocket endpoint.</summary>
    public string CommsUrl { get; init; } = "ws://127.0.0.1:5000/ws";

    /// <summary>Broker the stub gatekeeper bridges over. Empty disables the bridge.</summary>
    public string NatsUrl { get; init; } = "nats://127.0.0.1:4222";

    /// <summary>
    ///     Stub gatekeeper mode: <c>synthetic</c> (default, no credentials), <c>livekit</c> (mints a real
    ///     token from the environment), or <c>off</c> (expect a real comms-gatekeeper on the broker).
    /// </summary>
    public string BridgeMode { get; init; } = "synthetic";

    /// <summary>
    ///     Deadline for a conn string to arrive after a bot connects. Default covers three
    ///     <c>DwellPasses</c> at 1 Hz plus slack.
    /// </summary>
    public int ExpectConnStringWithinSeconds { get; init; } = 15;

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
            Transport = Arg("transport", "enet"),
            Mode = Arg("mode", "bots"),
            CommsEnabled = Flag("comms-enabled"),
            CommsUrl = Arg("comms-url", "ws://127.0.0.1:5000/ws"),
            NatsUrl = Arg("nats-url", "nats://127.0.0.1:4222"),
            BridgeMode = Arg("bridge-mode", "synthetic"),
            ExpectConnStringWithinSeconds = int.Parse(Arg("expect-conn-string-within", "15")),
        };
    }
}
