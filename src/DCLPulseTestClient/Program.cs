using System.Numerics;
using Decentraland.Pulse;
using PulseTestClient;
using PulseTestClient.Auth;
using PulseTestClient.Inputs;
using PulseTestClient.Networking;
using PulseTestClient.Profiles;
using PulseTestClient.Timing;

// 30 fps
const float TICK_RATE = 1 / 30f;

string Arg(string name, string fallback) =>
    args.FirstOrDefault(a => a.StartsWith($"--{name}="))?[(name.Length + 3)..] ?? fallback;

string metaforgeAccountName = Arg("account", "enetclient-test");
string serverIpAddress = Arg("ip", "127.0.0.1");
int serverPort = int.Parse(Arg("port", "7777"));
float rotateSpeed = float.Parse(Arg("rotate-speed", "90"));

// Fallback to Genesis Plaza spawn point
float initialPositionX = float.Parse(Arg("pos-x", "-104"));
float initialPositionY = float.Parse(Arg("pos-y", "0"));
float initialPositionZ = float.Parse(Arg("pos-z", "5"));

IAuthenticator authenticator = new MetaForgeAuthenticator();
IProfileGateway profileGateway = new MetaForgeProfileGateway();
MessagePipe pipe = new MessagePipe();
ITransport transport = new ENetTransport(new ENetTransportOptions(), pipe);
PulseMultiplayerService service = new PulseMultiplayerService(transport, pipe);

// Settings extracted from explorer
ParcelEncoder parcelEncoder = new ParcelEncoder(-150, -150, 163, 2, 16);
Dictionary<uint, Web3Address> peerAddresses = new Dictionary<uint, Web3Address>();
ITimeProvider timeProvider = new StopWatchTimeProvider();
InputState inputCollector = new ();
using CancellationTokenSource lifeCycleCts = new ();

Console.WriteLine("Login into account..");

string authChain = await authenticator.LoginAsync(metaforgeAccountName, lifeCycleCts.Token);

Console.WriteLine("Connecting to Pulse server..");

await service.ConnectAsync(serverIpAddress, serverPort, authChain, lifeCycleCts.Token);

Console.WriteLine("Subscribing into incoming server events..");

_ = Task.WhenAll(SubscribeToEmoteStartedAsync(lifeCycleCts.Token),
    SubscribeToEmoteStopped(lifeCycleCts.Token),
    SubscribeToPeerJoinedAsync(lifeCycleCts.Token),
    SubscribeToPeerLeftAsync(lifeCycleCts.Token),
    SubscribeToProfileAnnouncementAsync(lifeCycleCts.Token),
    SubscribeToPeerDeltaState(lifeCycleCts.Token),
    SubscribeToPeerFullState(lifeCycleCts.Token));

Console.WriteLine("Getting profile info..");

Profile profile = await profileGateway.GetAsync(metaforgeAccountName, lifeCycleCts.Token);

IInputReader inputReader = new BotWithManualExitInput(new Bot(profile.Emotes), new ConsoleInputReader(profile.Emotes));

// IInputReader inputReader = new PlayLoopEmote("urn:decentraland:matic:collections-v2:0x0ae365f8acc27f2c95fc7d60cf49a74f3af21573:3");

Console.WriteLine("Announcing profile..");

pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
{
    ProfileAnnouncement = new ProfileVersionAnnouncement
    {
        Version = profile.Version
    }
}, ITransport.PacketMode.RELIABLE));

Console.WriteLine("Ready! Starting Simulation.. Press ESC to quit.");

var position = new Vector3(initialPositionX, initialPositionY, initialPositionZ);
float rotationY = 0f;
uint lastFrameTick = timeProvider.TimeSinceStartupMs;

while (true)
{
    float deltaTimeSecs = (timeProvider.TimeSinceStartupMs - lastFrameTick) / 1000f;
    lastFrameTick = timeProvider.TimeSinceStartupMs;

    inputCollector.Reset();
    inputReader.Update(deltaTimeSecs, inputCollector);

    if (inputCollector.Quit) break;

    float dx = inputCollector.Velocity.X;
    float dz = inputCollector.Velocity.Z;
    float dRot = inputCollector.RotationDelta;
    bool moving = dx != 0f || dz != 0f;

    if (moving)
    {
        // Face the direction of movement
        rotationY = MathF.Atan2(dx, dz) * (180f / MathF.PI);
    }
    else
    {
        // Rotate in place when idle
        rotationY += dRot * rotateSpeed * TICK_RATE;
    }

    // Move relative to facing direction
    float radY = rotationY * MathF.PI / 180f;
    float forward = dz * TICK_RATE;
    float strafe = dx * TICK_RATE;

    position.X += MathF.Sin(radY) * forward + MathF.Cos(radY) * strafe;
    position.Z += MathF.Cos(radY) * forward - MathF.Sin(radY) * strafe;

    // Build velocity in world space
    var velocity = new Vector3(
        MathF.Sin(radY) * dz + MathF.Cos(radY) * dx,
        0f,
        MathF.Cos(radY) * dz - MathF.Sin(radY) * dx);

    uint stateFlags = (uint)PlayerAnimationFlags.Grounded;

    int parcelIndex = parcelEncoder.EncodeGlobalPosition(position, out var relativePosition);

    pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
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
                StateFlags = stateFlags,
                ParcelIndex = parcelIndex,
                Velocity = new Decentraland.Common.Vector3 { X = velocity.X, Y = velocity.Y, Z = velocity.Z }
            }
        }
    }, ITransport.PacketMode.UNRELIABLE_SEQUENCED));

    if (inputCollector.EmoteId is { } emoteId)
    {
        pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
        {
            EmoteStart = new EmoteStart
            {
                EmoteId = emoteId
            }
        }, ITransport.PacketMode.RELIABLE));
    }

    await Task.Delay(TimeSpan.FromSeconds(TICK_RATE));
}

await lifeCycleCts.CancelAsync();
service.Dispose();
return;

async Task SubscribeToPeerDeltaState(CancellationToken ct)
{
    await foreach (PlayerStateDeltaTier0 playerStateDelta in service.SubscribeAsync<PlayerStateDeltaTier0>(
                       ServerMessage.MessageOneofCase.PlayerStateDelta, ct))
    {
        // Web3Address peerAddress = peerAddresses[playerStateDelta.SubjectId];
        // Console.WriteLine($"Player state delta: {peerAddress}");
    }
}

async Task SubscribeToPeerFullState(CancellationToken ct)
{
    await foreach (PlayerStateFull playerStateFull in service.SubscribeAsync<PlayerStateFull>(
                       ServerMessage.MessageOneofCase.PlayerStateDelta, ct))
    {
        // Web3Address peerAddress = peerAddresses[playerStateFull.SubjectId];
        // Console.WriteLine($"Player state full: {peerAddress}");
    }
}

async Task SubscribeToPeerJoinedAsync(CancellationToken ct)
{
    await foreach (PlayerJoined playerJoined in service.SubscribeAsync<PlayerJoined>(
                       ServerMessage.MessageOneofCase.PlayerJoined, ct))
    {
        peerAddresses[playerJoined.State.SubjectId] = new Web3Address(playerJoined.UserId);
        Console.WriteLine($"Player joined: {playerJoined.UserId}");
    }
}

async Task SubscribeToPeerLeftAsync(CancellationToken ct)
{
    await foreach (PlayerLeft playerLeft in service.SubscribeAsync<PlayerLeft>(
                       ServerMessage.MessageOneofCase.PlayerLeft, ct))
    {
        Web3Address address = peerAddresses[playerLeft.SubjectId];
        Console.WriteLine($"Player left: {address}");
    }
}

async Task SubscribeToEmoteStartedAsync(CancellationToken ct)
{
    await foreach (EmoteStarted emoteStarted in service.SubscribeAsync<EmoteStarted>(
                       ServerMessage.MessageOneofCase.EmoteStarted, ct))
    {
        Web3Address address = peerAddresses[emoteStarted.SubjectId];
        Console.WriteLine($"Emote started {emoteStarted.EmoteId} from {address}");
    }
}

async Task SubscribeToEmoteStopped(CancellationToken ct)
{
    await foreach (EmoteStopped emoteStopped in service.SubscribeAsync<EmoteStopped>(
                       ServerMessage.MessageOneofCase.EmoteStopped, ct))
    {
        Web3Address address = peerAddresses[emoteStopped.SubjectId];
        Console.WriteLine($"Emote stopped {emoteStopped.Reason} from {address}");
    }
}

async Task SubscribeToProfileAnnouncementAsync(CancellationToken ct)
{
    await foreach (PlayerProfileVersionsAnnounced announcement in
                   service.SubscribeAsync<PlayerProfileVersionsAnnounced>(
                       ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced, ct))
    {
        peerAddresses.TryGetValue(announcement.SubjectId, out Web3Address address);
        Console.WriteLine($"Profile announced: {announcement.Version} from {announcement.SubjectId} addr {address}");
    }
}
