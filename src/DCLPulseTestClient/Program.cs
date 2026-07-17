using Decentraland.Pulse;
using Pulse.Transport;
using Pulse.Transport.WebTransport;
using PulseTestClient;
using PulseTestClient.Auth;
using PulseTestClient.Inputs;
using PulseTestClient.Networking;
using PulseTestClient.Profiles;
using PulseTestClient.Timing;
using System.Numerics;

var options = ClientOptions.FromArgs(args);
var behaviorSettings = BotBehaviorSettings.Load();
int botsPerProcess = BotBehaviorSettings.LoadBotsPerProcess();

// If bot count exceeds per-process limit and we're not already a child worker, orchestrate
bool isWorker = options.BotOffset > 0 || options.TotalBotCount > 0;
int processCount = (options.BotCount + botsPerProcess - 1) / botsPerProcess;

if (!isWorker && processCount > 1)
{
    using var orchestratorCts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        orchestratorCts.Cancel();
    };

    return await ProcessOrchestrator.RunAsync(options, botsPerProcess, orchestratorCts.Token);
}

// --- Worker mode: run bots in this process ---

int totalBotCount = options.TotalBotCount > 0 ? options.TotalBotCount : options.BotCount;

IAuthenticator authenticator = new MetaForgeAuthenticator();
using var profileGateway = new CatalystProfileGateway();
ParcelEncoder parcelEncoder = new ParcelEncoder(-150, -150, 163, 2, 16);
ITimeProvider timeProvider = new StopWatchTimeProvider();

using CancellationTokenSource lifeCycleCts = new ();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifeCycleCts.Cancel();
};

WatchForStopFile(lifeCycleCts);

bool useWebTransport = options.Transport.Equals("webtransport", StringComparison.OrdinalIgnoreCase);
byte[]? webTransportCertHash = useWebTransport ? ReadDevCertHash() : null;

// ENet uses one shared host for all bots; WebTransport is one QUIC session per bot, so there is no
// shared host in that mode.
ENetTransport? sharedTransport = null;

if (!useWebTransport)
{
    sharedTransport = new ENetTransport(new ENetTransportOptions { PeerLimit = options.BotCount });
    sharedTransport.Initialize();
}

Console.WriteLine($"Transport: {(useWebTransport ? "WebTransport" : "ENet")}");

// When running as a worker child, accounts are pre-created by the orchestrator
var accountNames = new string[options.BotCount];

if (!isWorker)
{
    for (var i = 0; i < options.BotCount; i++)
    {
        accountNames[i] = options.BotCount == 1 ? options.AccountPrefix : $"{options.AccountPrefix}-{i}";
        Console.WriteLine($"[{accountNames[i]}] Ensuring account exists..");
        await MetaForge.RunCommandAsync($"account create {accountNames[i]} --skip-update-check --skip-auto-login", lifeCycleCts.Token);
    }
}
else
{
    for (var i = 0; i < options.BotCount; i++)
        accountNames[i] = $"{options.AccountPrefix}-{options.BotOffset + i}";
}

// Auth, profile fetch, and connect can run in parallel
Task<BotSession>[] sessionTasks = Enumerable.Range(0, options.BotCount)
                                            .Select(i => CreateBotSessionAsync(i, options.BotOffset + i, totalBotCount, accountNames[i]))
                                            .ToArray();
var sessions = (await Task.WhenAll(sessionTasks)).ToList();

Console.WriteLine(options.BotCount == 1
    ? "Starting simulation.. Press ESC to quit."
    : $"Starting simulation with {options.BotCount} bots.. Press q+Enter or Ctrl+C to quit.");

if (options.BotCount > 1)
    WatchForQuitCommand(lifeCycleCts);

var loop = new SimulationLoop(sessions, options, behaviorSettings, parcelEncoder, timeProvider);
await loop.RunAsync(lifeCycleCts.Token);

await lifeCycleCts.CancelAsync();

foreach (BotSession s in sessions)
{
    await s.Service.DisconnectAsync(DisconnectReason.GRACEFUL, CancellationToken.None);
    s.Service.Dispose();
}

await Task.Delay(200);
sharedTransport?.Dispose();
return 0;

// --- Bot session factory ---

async Task<BotSession> CreateBotSessionAsync(int localIndex, int globalIndex, int total, string accountName)
{
    Console.WriteLine($"[{accountName}] Signing auth chain..");
    LoginResult login = await authenticator.LoginAsync(accountName, lifeCycleCts.Token);

    Console.WriteLine($"[{accountName}] Fetching profile for {login.WalletAddress}..");
    Profile profile = await profileGateway.GetAsync(login.WalletAddress, lifeCycleCts.Token);

    var pipe = new MessagePipe();
    ITransport botTransport = useWebTransport
        ? new WebTransportBotTransport(pipe, webTransportCertHash)
        : new BotTransport(sharedTransport ?? throw new InvalidOperationException("ENet transport was not initialized."), pipe);
    var service = new PulseMultiplayerService(botTransport, pipe);

    float angle = total > 1 ? 2f * MathF.PI * globalIndex / total : 0f;
    float botSpawnOffset = total > 1 ? options.SpawnRadius : 0f;

    var spawnOrigin = new Vector3(options.PositionX, options.PositionY, options.PositionZ);
    var position = new Vector3(
        options.PositionX + (MathF.Cos(angle) * botSpawnOffset),
        options.PositionY,
        options.PositionZ + (MathF.Sin(angle) * botSpawnOffset));

    var bot = new Bot(profile.Emotes,
        jumpEnabled: behaviorSettings.JumpEnabled,
        jumpMinInterval: behaviorSettings.JumpMinInterval,
        jumpMaxInterval: behaviorSettings.JumpMaxInterval);

    IInputReader inputReader = options.BotCount == 1
        ? new BotWithManualExitInput(bot, new ConsoleInputReader(profile.Emotes))
        : bot;

    var session = new BotSession
    {
        AccountName = accountName,
        Profile = profile,
        Pipe = pipe,
        Service = service,
        InputReader = inputReader,
        SpawnOrigin = spawnOrigin,
        Position = position,
        LastFrameTick = timeProvider.TimeSinceStartupMs,
    };

    Console.WriteLine($"[{accountName}] Connecting to {options.ServerIp}:{options.ServerPort}..");
    await service.ConnectAsync(options.ServerIp, options.ServerPort, login.AuthChainJson, lifeCycleCts.Token);

    _ = ServerEventHandler.ProcessAll(session, lifeCycleCts.Token);

    int spawnParcelIndex = parcelEncoder.EncodeGlobalPosition(position, out Vector3 spawnRelativePosition);

    pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
    {
        Teleport = new TeleportRequest
        {
            ParcelIndex = spawnParcelIndex,
            PositionXQuantized = spawnRelativePosition.X,
            PositionYQuantized = spawnRelativePosition.Y,
            PositionZQuantized = spawnRelativePosition.Z,
            Realm = options.Realm,
        },
    }, PacketMode.RELIABLE));

    pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
    {
        ProfileAnnouncement = new ProfileVersionAnnouncement { Version = profile.Version },
    }, PacketMode.RELIABLE));

    Console.WriteLine($"[{accountName}] Ready.");
    return session;
}

// --- WebTransport helpers ---

byte[]? ReadDevCertHash()
{
    string path = WebTransportDevCert.HashFilePath;

    if (!File.Exists(path))
    {
        Console.WriteLine($"WebTransport: dev cert hash file not found at {path}. " +
                          "Start the server with WebTransport enabled first (it writes the hash on self-sign), " +
                          "or connection will fail certificate validation.");
        return null;
    }

    return Convert.FromBase64String(File.ReadAllText(path).Trim());
}

// --- Shutdown helpers ---

void WatchForStopFile(CancellationTokenSource cts)
{
    string stopFile = Path.Combine(Path.GetTempPath(), "dcl-pulse-test-client.stop");

    // Only the top-level process (not a worker child) cleans up the stop file on startup
    if (!isWorker)
        File.Delete(stopFile);

    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            if (File.Exists(stopFile))
            {
                Console.WriteLine("Stop file detected, shutting down..");
                await cts.CancelAsync();
                break;
            }

            await Task.Delay(500, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    });
}

void WatchForQuitCommand(CancellationTokenSource cts)
{
    _ = Task.Run(() =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            string? line = Console.ReadLine();

            if (line is "q" or "Q" or "quit")
            {
                Console.WriteLine("Quit requested, shutting down..");
                cts.Cancel();
            }
        }
    });
}
