using Decentraland.Pulse;
using Pulse.Transport;
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

var sharedTransport = new ENetTransport(new ENetTransportOptions { PeerLimit = options.BotCount });
sharedTransport.Initialize();

// Scene-listener mode: connect receive-only for a fixed parcel set, never simulate or send.
if (!string.IsNullOrWhiteSpace(options.SceneListenerParcels))
    return await RunSceneListenerAsync();

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
sharedTransport.Dispose();
return 0;

// --- Bot session factory ---

async Task<BotSession> CreateBotSessionAsync(int localIndex, int globalIndex, int total, string accountName)
{
    Console.WriteLine($"[{accountName}] Signing auth chain..");
    LoginResult login = await authenticator.LoginAsync(accountName, lifeCycleCts.Token);

    Console.WriteLine($"[{accountName}] Fetching profile for {login.WalletAddress}..");
    Profile profile = await profileGateway.GetAsync(login.WalletAddress, lifeCycleCts.Token);

    var pipe = new MessagePipe();
    var botTransport = new BotTransport(sharedTransport, pipe);
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

// --- Scene-listener mode ---

async Task<int> RunSceneListenerAsync()
{
    List<int> parcelIndices = ParseListenerParcels(options.SceneListenerParcels);

    if (parcelIndices.Count == 0)
    {
        Console.Error.WriteLine(
            "No valid parcels in --scene-listener-parcels; expected comma-separated x:z coordinates (e.g. -7:0,-6:0).");
        return 1;
    }

    string listenerAccount = options.AccountPrefix;

    Console.WriteLine($"[{listenerAccount}] Ensuring account exists..");
    await MetaForge.RunCommandAsync($"account create {listenerAccount} --skip-update-check --skip-auto-login", lifeCycleCts.Token);

    Console.WriteLine($"[{listenerAccount}] Signing auth chain..");
    LoginResult login = await authenticator.LoginAsync(listenerAccount, lifeCycleCts.Token);

    var pipe = new MessagePipe();
    var listenerTransport = new BotTransport(sharedTransport, pipe);
    var service = new PulseMultiplayerService(listenerTransport, pipe);

    Console.WriteLine($"[{listenerAccount}] Connecting as scene listener to {options.ServerIp}:{options.ServerPort} " +
                      $"for parcels [{options.SceneListenerParcels}] in realm '{options.Realm}'..");

    await service.ConnectAsSceneListenerAsync(options.ServerIp, options.ServerPort, login.AuthChainJson,
        options.Realm, parcelIndices, lifeCycleCts.Token);

    Console.WriteLine($"[{listenerAccount}] Scene listener ready. Observing {parcelIndices.Count} parcel(s). Press Ctrl+C to quit.");

    await ProcessListenerEventsAsync(listenerAccount, service);

    await service.DisconnectAsync(DisconnectReason.GRACEFUL, CancellationToken.None);
    service.Dispose();
    await Task.Delay(200);
    sharedTransport.Dispose();
    return 0;
}

List<int> ParseListenerParcels(string spec)
{
    var indices = new List<int>();

    foreach (string token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] parts = token.Split(':');

        if (parts.Length != 2
            || !int.TryParse(parts[0], out int parcelX)
            || !int.TryParse(parts[1], out int parcelZ))
        {
            Console.Error.WriteLine($"Ignoring malformed parcel token '{token}'; expected 'x:z'.");
            continue;
        }

        int index = parcelEncoder.Encode(parcelX, parcelZ);

        if (!indices.Contains(index))
            indices.Add(index);
    }

    return indices;
}

// Receive-only: subscribe to the positional stream and log each message with subject id + parcel.
// Never sends anything back to the server after the handshake.
//
// We ALSO subscribe to EmoteStarted/EmoteStopped/PlayerProfileVersionAnnounced — messages a scene
// listener must NEVER receive. Without an active subscription the service silently drops them
// (RouteIncomingMessagesAsync discards messages with no subscriber), so a "0 received" tally would
// be true by construction rather than evidence. Subscribing turns any server-side leak into a
// visible LEAK line and a non-zero counter.
async Task ProcessListenerEventsAsync(string accountName, PulseMultiplayerService service)
{
    string Parcel(int index)
    {
        parcelEncoder.Decode(index, out int x, out int z);
        return $"{x}:{z}";
    }

    long positionalCount = 0;
    long leakCount = 0;

    try
    {
        await foreach (ServerMessage message in service.SubscribeAllAsync(lifeCycleCts.Token,
                           ServerMessage.MessageOneofCase.PlayerJoined,
                           ServerMessage.MessageOneofCase.PlayerLeft,
                           ServerMessage.MessageOneofCase.PlayerStateDelta,
                           ServerMessage.MessageOneofCase.PlayerStateFull,
                           ServerMessage.MessageOneofCase.Teleported,
                           // Leak-detection subscriptions: a scene listener must never see these.
                           ServerMessage.MessageOneofCase.EmoteStarted,
                           ServerMessage.MessageOneofCase.EmoteStopped,
                           ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced))
        {
            switch (message.MessageCase)
            {
                case ServerMessage.MessageOneofCase.PlayerJoined:
                    PlayerJoined joined = message.PlayerJoined;
                    positionalCount++;
                    Console.WriteLine($"[{accountName}] PlayerJoined subject={joined.State.SubjectId} " +
                                      $"parcel={Parcel(joined.State.State.ParcelIndex)} user={joined.UserId}");
                    break;
                case ServerMessage.MessageOneofCase.PlayerLeft:
                    positionalCount++;
                    Console.WriteLine($"[{accountName}] PlayerLeft subject={message.PlayerLeft.SubjectId}");
                    break;
                case ServerMessage.MessageOneofCase.PlayerStateDelta:
                    PlayerStateDeltaTier0 delta = message.PlayerStateDelta;
                    positionalCount++;
                    // parcel_index is an optional delta field, only present when it changed.
                    string deltaParcel = delta.HasParcelIndex ? Parcel(delta.ParcelIndex) : "(unchanged)";
                    Console.WriteLine($"[{accountName}] PlayerStateDelta subject={delta.SubjectId} " +
                                      $"parcel={deltaParcel} seq={delta.NewSeq}");
                    break;
                case ServerMessage.MessageOneofCase.PlayerStateFull:
                    PlayerStateFull full = message.PlayerStateFull;
                    positionalCount++;
                    Console.WriteLine($"[{accountName}] PlayerStateFull subject={full.SubjectId} " +
                                      $"parcel={Parcel(full.State.ParcelIndex)} seq={full.Sequence}");
                    break;
                case ServerMessage.MessageOneofCase.Teleported:
                    TeleportPerformed teleport = message.Teleported;
                    positionalCount++;
                    Console.WriteLine($"[{accountName}] Teleported subject={teleport.SubjectId} " +
                                      $"parcel={Parcel(teleport.State.ParcelIndex)} seq={teleport.Sequence}");
                    break;
                case ServerMessage.MessageOneofCase.EmoteStarted:
                    leakCount++;
                    Console.WriteLine($"[{accountName}] LEAK: EmoteStarted for subject {message.EmoteStarted.SubjectId} " +
                                      "— server must never send this to a scene listener");
                    break;
                case ServerMessage.MessageOneofCase.EmoteStopped:
                    leakCount++;
                    Console.WriteLine($"[{accountName}] LEAK: EmoteStopped for subject {message.EmoteStopped.SubjectId} " +
                                      "— server must never send this to a scene listener");
                    break;
                case ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced:
                    leakCount++;
                    Console.WriteLine($"[{accountName}] LEAK: PlayerProfileVersionAnnounced for subject " +
                                      $"{message.PlayerProfileVersionAnnounced.SubjectId} " +
                                      "— server must never send this to a scene listener");
                    break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Graceful shutdown (Ctrl+C / stop file) cancels the subscription.
    }

    Console.WriteLine($"[{accountName}] Listener summary: positional messages={positionalCount}, " +
                      $"suppressed-message LEAKS={leakCount}");
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
