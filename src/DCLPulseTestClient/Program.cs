using System.Numerics;
using Decentraland.Pulse;
using PulseTestClient;
using PulseTestClient.Auth;
using PulseTestClient.Inputs;
using PulseTestClient.Networking;
using PulseTestClient.Profiles;
using PulseTestClient.Timing;

const float TICK_RATE = 1 / 30f;

string Arg(string name, string fallback) =>
    args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..] ?? fallback;

string accountPrefix = Arg("account", "enetclient-test");
string serverIpAddress = Arg("ip", "127.0.0.1");
int serverPort = int.Parse(Arg("port", "7777"));
float rotateSpeed = float.Parse(Arg("rotate-speed", "90"));
var botCount = int.Parse(Arg("bot-count", "1"));

float initialPositionX = float.Parse(Arg("pos-x", "-104"));
float initialPositionY = float.Parse(Arg("pos-y", "0"));
float initialPositionZ = float.Parse(Arg("pos-z", "5"));

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

string stopFile = Path.Combine(Path.GetTempPath(), "dcl-pulse-test-client.stop");
File.Delete(stopFile);

_ = Task.Run(async () =>
{
    while (!lifeCycleCts.Token.IsCancellationRequested)
    {
        if (File.Exists(stopFile))
        {
            File.Delete(stopFile);
            Console.WriteLine("Stop file detected, shutting down..");
            await lifeCycleCts.CancelAsync();
            break;
        }

        await Task.Delay(500, lifeCycleCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
});

var sharedTransport = new ENetTransport(new ENetTransportOptions { PeerLimit = botCount });
var sessions = new List<BotSession>(botCount);

for (var i = 0; i < botCount; i++)
{
    string accountName = botCount == 1 ? accountPrefix : $"{accountPrefix}-{i}";

    Console.WriteLine($"[{accountName}] Logging in..");
    LoginResult login = await authenticator.LoginAsync(accountName, lifeCycleCts.Token);

    Console.WriteLine($"[{accountName}] Fetching profile for {login.WalletAddress}..");
    Profile profile = await profileGateway.GetAsync(login.WalletAddress, lifeCycleCts.Token);

    var pipe = new MessagePipe();
    var botTransport = new BotTransport(sharedTransport, pipe);
    var service = new PulseMultiplayerService(botTransport, pipe);

    // Spread bots in a circle around the spawn point
    float angle = botCount > 1 ? 2f * MathF.PI * i / botCount : 0f;
    float spawnRadius = botCount > 1 ? 2f * botCount : 0f;

    var position = new Vector3(
        initialPositionX + (MathF.Cos(angle) * spawnRadius),
        initialPositionY,
        initialPositionZ + (MathF.Sin(angle) * spawnRadius));

    IInputReader inputReader = botCount == 1
        ? new BotWithManualExitInput(new Bot(profile.Emotes), new ConsoleInputReader(profile.Emotes))
        : new Bot(profile.Emotes);

    var session = new BotSession
    {
        AccountName = accountName,
        Profile = profile,
        Pipe = pipe,
        Service = service,
        InputReader = inputReader,
        Position = position,
        LastFrameTick = timeProvider.TimeSinceStartupMs,
    };

    Console.WriteLine($"[{accountName}] Connecting to {serverIpAddress}:{serverPort}..");
    await service.ConnectAsync(serverIpAddress, serverPort, login.AuthChainJson, lifeCycleCts.Token);

    SubscribeToServerEvents(session, lifeCycleCts.Token);

    pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
    {
        ProfileAnnouncement = new ProfileVersionAnnouncement { Version = profile.Version },
    }, ITransport.PacketMode.RELIABLE));

    sessions.Add(session);
    Console.WriteLine($"[{accountName}] Ready.");
}

Console.WriteLine(botCount == 1
    ? "Starting simulation.. Press ESC to quit."
    : $"Starting simulation with {botCount} bots.. Press q+Enter or Ctrl+C to quit.");

if (botCount > 1)
{
    _ = Task.Run(() =>
    {
        while (!lifeCycleCts.Token.IsCancellationRequested)
        {
            string? line = Console.ReadLine();

            if (line is "q" or "Q" or "quit")
            {
                Console.WriteLine("Quit requested, shutting down..");
                lifeCycleCts.Cancel();
            }
        }
    });
}

while (!lifeCycleCts.Token.IsCancellationRequested)
{
    var quit = false;

    foreach (BotSession bot in sessions)
    {
        float deltaTimeSecs = (timeProvider.TimeSinceStartupMs - bot.LastFrameTick) / 1000f;
        bot.LastFrameTick = timeProvider.TimeSinceStartupMs;

        bot.InputCollector.Reset();
        bot.InputReader.Update(deltaTimeSecs, bot.InputCollector);

        if (bot.InputCollector.Quit)
        {
            quit = true;
            break;
        }

        float dx = bot.InputCollector.Velocity.X;
        float dz = bot.InputCollector.Velocity.Z;
        float dRot = bot.InputCollector.RotationDelta;
        bool moving = dx != 0f || dz != 0f;

        float rotationY = bot.RotationY;

        if (moving)
            rotationY = MathF.Atan2(dx, dz) * (180f / MathF.PI);
        else
            rotationY += dRot * rotateSpeed * TICK_RATE;

        bot.RotationY = rotationY;

        float radY = rotationY * MathF.PI / 180f;
        float forward = dz * TICK_RATE;
        float strafe = dx * TICK_RATE;

        Vector3 pos = bot.Position;
        pos.X += (MathF.Sin(radY) * forward) + (MathF.Cos(radY) * strafe);
        pos.Z += (MathF.Cos(radY) * forward) - (MathF.Sin(radY) * strafe);
        bot.Position = pos;

        var velocity = new Vector3(
            (MathF.Sin(radY) * dz) + (MathF.Cos(radY) * dx),
            0f,
            (MathF.Cos(radY) * dz) - (MathF.Sin(radY) * dx));

        int parcelIndex = parcelEncoder.EncodeGlobalPosition(bot.Position, out Vector3 relativePosition);

        bot.Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
        {
            Input = new PlayerStateInput
            {
                State = new PlayerState
                {
                    HeadPitch = 0f,
                    HeadYaw = rotationY,
                    GlideState = new GlideState(),
                    MovementBlend = moving ? 1f : 0f,
                    Position = new Decentraland.Common.Vector3
                        { X = relativePosition.X, Y = relativePosition.Y, Z = relativePosition.Z },
                    RotationY = rotationY,
                    SlideBlend = 0f,
                    StateFlags = (uint)PlayerAnimationFlags.Grounded,
                    ParcelIndex = parcelIndex,
                    Velocity = new Decentraland.Common.Vector3 { X = velocity.X, Y = velocity.Y, Z = velocity.Z },
                },
            },
        }, ITransport.PacketMode.UNRELIABLE_SEQUENCED));

        if (bot.InputCollector.EmoteId is { } emoteId)
        {
            bot.Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
            {
                EmoteStart = new EmoteStart { EmoteId = emoteId },
            }, ITransport.PacketMode.RELIABLE));
        }
    }

    if (quit) break;

    await Task.Delay(TimeSpan.FromSeconds(TICK_RATE));
}

await lifeCycleCts.CancelAsync();

foreach (BotSession s in sessions)
{
    await s.Service.DisconnectAsync(ITransport.DisconnectReason.Graceful, CancellationToken.None);
    s.Service.Dispose();
}

// Give ENet a moment to flush disconnect packets before tearing down the host
await Task.Delay(200);
sharedTransport.Dispose();
return;

// --- Per-bot subscriptions ---

void SubscribeToServerEvents(BotSession bot, CancellationToken ct)
{
    _ = Task.WhenAll(
        SubscribeToPeerDeltaState(bot, ct),
        SubscribeToPeerFullState(bot, ct),
        SubscribeToPeerJoinedAsync(bot, ct),
        SubscribeToPeerLeftAsync(bot, ct),
        SubscribeToEmoteStartedAsync(bot, ct),
        SubscribeToEmoteStopped(bot, ct),
        SubscribeToProfileAnnouncementAsync(bot, ct));
}

async Task SubscribeToPeerDeltaState(BotSession bot, CancellationToken ct)
{
    await foreach (PlayerStateDeltaTier0 delta in bot.Service.SubscribeAsync<PlayerStateDeltaTier0>(
                       ServerMessage.MessageOneofCase.PlayerStateDelta, ct))
    {
        uint subjectId = delta.SubjectId;

        if (bot.KnownSeqBySubject.TryGetValue(subjectId, out uint lastSeq) && delta.BaselineSeq != lastSeq)
        {
            Console.WriteLine($"[{bot.AccountName}] Seq gap for subject {subjectId}: expected {lastSeq}, got {delta.BaselineSeq}. Requesting resync.");

            bot.Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
            {
                Resync = new ResyncRequest { SubjectId = subjectId, KnownSeq = lastSeq },
            }, ITransport.PacketMode.RELIABLE));

            continue;
        }

        bot.KnownSeqBySubject[subjectId] = delta.NewSeq;
    }
}

async Task SubscribeToPeerFullState(BotSession bot, CancellationToken ct)
{
    await foreach (PlayerStateFull full in bot.Service.SubscribeAsync<PlayerStateFull>(
                       ServerMessage.MessageOneofCase.PlayerStateFull, ct))
    {
        bot.KnownSeqBySubject[full.SubjectId] = full.Sequence;
        Console.WriteLine($"[{bot.AccountName}] Full state for subject {full.SubjectId}, seq={full.Sequence}");
    }
}

async Task SubscribeToPeerJoinedAsync(BotSession bot, CancellationToken ct)
{
    await foreach (PlayerJoined joined in bot.Service.SubscribeAsync<PlayerJoined>(
                       ServerMessage.MessageOneofCase.PlayerJoined, ct))
    {
        bot.PeerAddresses[joined.State.SubjectId] = new Web3Address(joined.UserId);
        bot.KnownSeqBySubject[joined.State.SubjectId] = joined.State.Sequence;
        Console.WriteLine($"[{bot.AccountName}] Player joined: {joined.UserId}");
    }
}

async Task SubscribeToPeerLeftAsync(BotSession bot, CancellationToken ct)
{
    await foreach (PlayerLeft left in bot.Service.SubscribeAsync<PlayerLeft>(
                       ServerMessage.MessageOneofCase.PlayerLeft, ct))
    {
        bot.PeerAddresses.TryGetValue(left.SubjectId, out Web3Address address);
        bot.KnownSeqBySubject.Remove(left.SubjectId);
        bot.PeerAddresses.Remove(left.SubjectId);
        Console.WriteLine($"[{bot.AccountName}] Player left: {address}");
    }
}

async Task SubscribeToEmoteStartedAsync(BotSession bot, CancellationToken ct)
{
    await foreach (EmoteStarted emote in bot.Service.SubscribeAsync<EmoteStarted>(
                       ServerMessage.MessageOneofCase.EmoteStarted, ct))
    {
        bot.PeerAddresses.TryGetValue(emote.SubjectId, out Web3Address address);
        Console.WriteLine($"[{bot.AccountName}] Emote started {emote.EmoteId} from {address}");
    }
}

async Task SubscribeToEmoteStopped(BotSession bot, CancellationToken ct)
{
    await foreach (EmoteStopped emote in bot.Service.SubscribeAsync<EmoteStopped>(
                       ServerMessage.MessageOneofCase.EmoteStopped, ct))
    {
        bot.PeerAddresses.TryGetValue(emote.SubjectId, out Web3Address address);
        Console.WriteLine($"[{bot.AccountName}] Emote stopped {emote.Reason} from {address}");
    }
}

async Task SubscribeToProfileAnnouncementAsync(BotSession bot, CancellationToken ct)
{
    await foreach (PlayerProfileVersionsAnnounced announcement in
                   bot.Service.SubscribeAsync<PlayerProfileVersionsAnnounced>(
                       ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced, ct))
    {
        bot.PeerAddresses.TryGetValue(announcement.SubjectId, out Web3Address address);
        Console.WriteLine($"[{bot.AccountName}] Profile announced: v{announcement.Version} from {address}");
    }
}
