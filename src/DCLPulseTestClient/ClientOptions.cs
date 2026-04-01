namespace PulseTestClient;

public class ClientOptions
{
    public string AccountPrefix { get; init; } = "enetclient-test";
    public string ServerIp { get; init; } = "127.0.0.1";
    public int ServerPort { get; init; } = 7777;
    public float RotateSpeed { get; init; } = 90f;
    public int BotCount { get; init; } = 1;
    public float PositionX { get; init; } = -104f;
    public float PositionY { get; init; }
    public float PositionZ { get; init; } = 5f;
    public float SpawnRadius { get; init; } = 10f;
    public float DispersionRadius { get; init; } = 20f;
    public int BotOffset { get; init; }
    public int TotalBotCount { get; init; }

    public static ClientOptions FromArgs(string[] args)
    {
        string Arg(string name, string fallback) =>
            args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..] ?? fallback;

        return new ClientOptions
        {
            AccountPrefix = Arg("account", "enetclient-test"),
            ServerIp = Arg("ip", "127.0.0.1"),
            ServerPort = int.Parse(Arg("port", "7777")),
            RotateSpeed = float.Parse(Arg("rotate-speed", "90")),
            BotCount = int.Parse(Arg("bot-count", "1")),
            PositionX = float.Parse(Arg("pos-x", "-104")),
            PositionY = float.Parse(Arg("pos-y", "0")),
            PositionZ = float.Parse(Arg("pos-z", "5")),
            SpawnRadius = float.Parse(Arg("spawn-radius", "10")),
            DispersionRadius = float.Parse(Arg("dispersion-radius", "20")),
            BotOffset = int.Parse(Arg("bot-offset", "0")),
            TotalBotCount = int.Parse(Arg("total-bot-count", "0")),
        };
    }
}
