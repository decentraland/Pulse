using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using Decentraland.Pulse;
using Pulse.Transport;
using PulseTestClient;
using PulseTestClient.Auth;
using PulseTestClient.Comms;
using PulseTestClient.Inputs;
using PulseTestClient.Networking;
using PulseTestClient.Profiles;
using PulseTestClient.Timing;

namespace DCLPulseTests.Comms;

/// <summary>Runs stationary, authenticated test clients through Pulse, Connector, Gatekeeper and LiveKit.</summary>
[TestFixture]
[Category("RoomRecoveryE2E")]
[NonParallelizable]
[Explicit("Requires the matching backend stack, LiveKit and MetaForge. See docs/room-recovery-automation.md.")]
public sealed class RoomRecoveryE2ETests
{
    private static readonly Vector3 ORIGIN = new(-104, 0, 5);
    private readonly List<Probe> probes = [];
    private readonly HashSet<string> accounts = [];
    private readonly ENetTransport transport = new(new PulseTestClient.Networking.ENetTransportOptions { PeerLimit = 32 });
    private string ip = "";
    private string commsUrl = "";
    private string accountPrefix = "";
    private string realm = "";
    private int port;
    private TimeSpan timeout;
    private TimeSpan stability;
    private CancellationTokenSource lifetime = new();

    [OneTimeSetUp]
    public void Configure()
    {
        ip = Required("PULSE_E2E_IP");
        commsUrl = Required("PULSE_E2E_COMMS_URL");
        realm = Required("PULSE_E2E_REALM");
        port = PositiveInt("PULSE_E2E_PORT", 7777);
        timeout = TimeSpan.FromSeconds(PositiveInt("PULSE_E2E_TIMEOUT_SECONDS", 120));
        stability = TimeSpan.FromSeconds(PositiveInt("PULSE_E2E_STABILITY_SECONDS", 180));
        transport.Initialize();
    }

    [SetUp]
    public void StartCase()
    {
        accountPrefix = $"recovery-{Guid.NewGuid():N}";
        lifetime.Dispose();
        lifetime = new CancellationTokenSource(timeout * 6 + stability * 4);
        TestContext.Progress.WriteLine($"Starting {TestContext.CurrentContext.Test.Name}");
    }

    [TearDown]
    public async Task StopCase()
    {
        await lifetime.CancelAsync();
        var errors = new List<Exception>();
        foreach (Probe probe in probes.AsEnumerable().Reverse())
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await probe.DisposeAsync(cleanup.Token); }
            catch (Exception e) { errors.Add(e); }
        }
        probes.Clear();
        foreach (string account in accounts)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await MetaForge.RunCommandAsync($"account remove {account} --yes --skip-update-check", cleanup.Token); }
            catch (Exception e) { errors.Add(e); }
        }
        accounts.Clear();
        lifetime.Dispose();
        if (errors.Count > 0) throw new AggregateException("Room recovery cleanup failed", errors);
    }

    [OneTimeTearDown]
    public void StopTransport() => transport.Dispose();

    [Test]
    public async Task NormalJoin_ConnectsAndSeesTheOtherParticipant()
    {
        Probe observer = await StartAsync("observer", "c", ORIGIN);
        Probe a = await StartAsync("owner", "a", ORIGIN);
        await TogetherAsync(observer, a);
        await StableAsync(observer, a);
    }

    [Test]
    public async Task SameRoomTakeover_RemovesAAndKeepsBStable()
    {
        Probe observer = await StartAsync("observer", "c", ORIGIN);
        Probe a = await StartAsync("owner", "a", ORIGIN);
        await TogetherAsync(observer, a);
        LiveKitJoiner.Observation before = await a.Room.ObserveAsync(lifetime.Token);

        Probe b = await StartAsync("owner", "b", ORIGIN);
        await TogetherAsync(observer, b);
        await DisplacedAsync(a);
        LiveKitJoiner.Observation after = await b.Room.ObserveAsync(lifetime.Token);
        Assert.That(after.RoomSid, Is.EqualTo(before.RoomSid), "the observer must keep the original island alive");
        Assert.That(after.SelfSid, Is.Not.EqualTo(before.SelfSid));
        await StableAsync(observer, b, a);
    }

    [Test]
    public async Task RepeatedTakeover_AdmittedAThenBThenAUsesDistinctParticipants()
    {
        Probe observer = await StartAsync("observer", "c", ORIGIN);
        Probe a = await StartAsync("owner", "a", ORIGIN);
        await TogetherAsync(observer, a);
        LiveKitJoiner.Observation first = await a.Room.ObserveAsync(lifetime.Token);
        Probe b = await StartAsync("owner", "b", ORIGIN);
        await TogetherAsync(observer, b);
        LiveKitJoiner.Observation second = await b.Room.ObserveAsync(lifetime.Token);
        await DisplacedAsync(a);
        Probe returningA = await StartAsync("owner", "a-next-login", ORIGIN);
        await TogetherAsync(observer, returningA);
        LiveKitJoiner.Observation third = await returningA.Room.ObserveAsync(lifetime.Token);
        Assert.That(new[] { first.SelfSid, second.SelfSid, third.SelfSid }, Is.Unique);
        Assert.That(new[] { second.RoomSid, third.RoomSid }, Is.All.EqualTo(first.RoomSid));
        await DisplacedAsync(b);
        await StableAsync(observer, returningA, a, b);
    }

    [Test]
    public async Task DifferentRoomTakeover_CleansTheOldRoom()
    {
        Probe observer = await StartAsync("observer", "c", ORIGIN);
        Probe a = await StartAsync("owner", "a", ORIGIN);
        await TogetherAsync(observer, a);
        LiveKitJoiner.Observation oldRoom = await observer.Room.ObserveAsync(lifetime.Token);
        Probe b = await StartAsync("owner", "b", ORIGIN + new Vector3(1000, 0, 1000));
        await UntilAsync(async token =>
        {
            b.Check();
            observer.Check();
            LiveKitJoiner.Observation current = await b.Room.ObserveAsync(token);
            LiveKitJoiner.Observation old = await observer.Room.ObserveAsync(token);
            return current.Connected && old.Connected && current.RoomSid != old.RoomSid &&
                   old.RoomSid == oldRoom.RoomSid && !old.Participants.ContainsKey(b.Wallet);
        }, "B must connect elsewhere and leave no participant in C's old room");
        await DisplacedAsync(a);
        await StableAsync(observer, b, a);
    }

    [Test]
    public async Task CommsRestart_RecoversTheSameIslandWithoutRestartingPulse()
    {
        Probe observer = await StartAsync("observer", "c", ORIGIN);
        Probe a = await StartAsync("owner", "a", ORIGIN);
        await TogetherAsync(observer, a);
        LiveKitJoiner.Observation before = await a.Room.ObserveAsync(lifetime.Token);
        using var restart = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        restart.CancelAfter(timeout);
        await a.StopCommsAsync(restart.Token);
        await a.StartCommsAsync(commsUrl, restart.Token);
        await TogetherAsync(observer, a);
        LiveKitJoiner.Observation after = await a.Room.ObserveAsync(lifetime.Token);
        Assert.That(after.RoomSid, Is.EqualTo(before.RoomSid));
        Assert.That(after.SelfSid, Is.Not.EqualTo(before.SelfSid));
        await StableAsync(observer, a);
    }

    [Test]
    public async Task IslandOnlyDisconnect_RecoversFromFreshCredentialsWhileStationary()
    {
        Probe observer = await StartAsync("observer", "c", ORIGIN);
        Probe a = await StartAsync("owner", "a", ORIGIN);
        await TogetherAsync(observer, a);
        int assignments = a.AssignmentCount;
        await a.Room.DisconnectAsync(lifetime.Token);
        await UntilAsync(_ =>
        {
            a.Check();
            return Task.FromResult(a.AssignmentCount > assignments);
        }, "a fresh island assignment must arrive");
        await TogetherAsync(observer, a);
        await StableAsync(observer, a);
    }

    [Test]
    public async Task DelayedConnectorConnect_RecoversAfterTheInitialDeliveryWindow()
    {
        Probe observer = await StartAsync("observer", "c", ORIGIN);
        Probe a = await StartAsync("owner", "a", ORIGIN);
        await TogetherAsync(observer, a);
        Probe b = await StartAsync("owner", "b", ORIGIN, connectComms: false);
        await DisplacedAsync(a);
        // Cleanup completed before B has a socket that could receive credentials.
        await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(timeout);
        await b.StartCommsAsync(commsUrl, startup.Token);
        await TogetherAsync(observer, b);
        await StableAsync(observer, b, a);
    }

    private async Task<Probe> StartAsync(string role, string device, Vector3 position, bool connectComms = true)
    {
        string account = $"{accountPrefix}-{role}";
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(timeout);
        if (accounts.Add(account))
            await MetaForge.RunCommandAsync($"account create {account} --skip-update-check --skip-auto-login", startup.Token);
        var probe = new Probe(account, device, position, lifetime.Token);
        probes.Add(probe);
        await probe.StartPulseAsync(transport, ip, port, realm, startup.Token);
        if (connectComms) await probe.StartCommsAsync(commsUrl, startup.Token);
        return probe;
    }

    private Task TogetherAsync(Probe observer, Probe winner) => UntilAsync(async token =>
    {
        observer.Check();
        winner.Check();
        return Together(await observer.Room.ObserveAsync(token), observer.Wallet,
            await winner.Room.ObserveAsync(token), winner.Wallet);
    }, "both clients must share a room and observe each other's current participant SID");

    private Task DisplacedAsync(Probe old) => UntilAsync(async token =>
        old.Pipe.LastDisconnectReason == DisconnectReason.DUPLICATE_SESSION &&
        !(await old.Room.ObserveAsync(token)).Connected,
        "the displaced client must lose both Pulse ownership and its LiveKit participant");

    private async Task StableAsync(Probe? observer, Probe winner, params Probe[] displaced)
    {
        TestContext.Progress.WriteLine($"{TestContext.CurrentContext.Test.Name}: stability window {stability.TotalSeconds}s");
        using var window = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        window.CancelAfter(stability + timeout);
        CancellationToken token = window.Token;
        LiveKitJoiner.Observation before = await winner.Room.ObserveAsync(token);
        LiveKitJoiner.Observation? observerBefore = observer is null ? null : await observer.Room.ObserveAsync(token);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < stability)
        {
            winner.Check();
            LiveKitJoiner.Observation current = await winner.Room.ObserveAsync(token);
            Assert.That(current.Connected, Is.True, "winner disconnected during stability window");
            Assert.That(current.SelfSid, Is.EqualTo(before.SelfSid), "winner was replaced during recovery");
            Assert.That(current.Interruptions, Is.EqualTo(before.Interruptions), "a brief disconnect must not be hidden by a later reconnect");
            if (observer is not null && observerBefore is not null)
            {
                observer.Check();
                LiveKitJoiner.Observation observed = await observer.Room.ObserveAsync(token);
                Assert.That(observed.Connected, Is.True);
                Assert.That(observed.RoomSid, Is.EqualTo(observerBefore.RoomSid));
                Assert.That(observed.SelfSid, Is.EqualTo(observerBefore.SelfSid), "observer participant was replaced");
                Assert.That(observed.Interruptions, Is.EqualTo(observerBefore.Interruptions), "observer connection was interrupted");
                if (observerBefore.RoomSid == before.RoomSid)
                    Assert.That(Together(observed, observer.Wallet, current, winner.Wallet), Is.True);
                else
                    Assert.That(observed.Participants.ContainsKey(winner.Wallet), Is.False, "wallet reappeared in the displaced room");
            }
            foreach (Probe old in displaced)
                Assert.That((await old.Room.ObserveAsync(token)).Connected, Is.False, "displaced participant rejoined");
            await Task.Delay(100, token);
        }
    }

    private static bool Together(LiveKitJoiner.Observation a, string aWallet, LiveKitJoiner.Observation b, string bWallet) =>
        a.Connected && b.Connected && !string.IsNullOrEmpty(a.RoomSid) && a.RoomSid == b.RoomSid &&
        a.Participants.TryGetValue(bWallet, out string? bSid) && bSid == b.SelfSid &&
        b.Participants.TryGetValue(aWallet, out string? aSid) && aSid == a.SelfSid;

    private async Task UntilAsync(Func<CancellationToken, Task<bool>> condition, string description)
    {
        using var check = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        check.CancelAfter(timeout);
        try
        {
            while (true)
            {
                check.Token.ThrowIfCancellationRequested();
                if (await condition(check.Token)) return;
                await Task.Delay(100, check.Token);
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        {
            Assert.Fail($"Timed out after {timeout.TotalSeconds}s: {description}");
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} explicitly; this suite has no deployed-environment default.");

    private static int PositiveInt(string name, int fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? int.Parse(value) is > 0 and var positive ? positive : throw new ArgumentOutOfRangeException(name)
        : fallback;

    private sealed class StationaryInput : IInputReader
    {
        public void Update(float deltaTime, InputState state) { }
    }

    private sealed class Probe(string account, string device, Vector3 position, CancellationToken ct) : IAsyncDisposable
    {
        private readonly IAuthenticator authenticator = new MetaForgeAuthenticator();
        private readonly CancellationTokenSource life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        private PulseMultiplayerService? pulse;
        private WebSocketCommsConnection? connection;
        private CancellationTokenSource? commsLife;
        private Task[] commsTasks = [];
        private Task[] pulseTasks = [];
        private string? failure;
        private int assignmentCount;
        public MessagePipe Pipe { get; } = new();
        public LiveKitJoiner Room { get; } = new($"{account}/{device}");
        public string Wallet { get; private set; } = "";
        public int AssignmentCount => Volatile.Read(ref assignmentCount);

        public async Task StartPulseAsync(ENetTransport transport, string ip, int port, string realm, CancellationToken startup)
        {
            LoginResult login = await authenticator.LoginAsync(account, device, startup);
            Wallet = login.WalletAddress.ToLowerInvariant();
            pulse = new PulseMultiplayerService(new BotTransport(transport, Pipe), Pipe);
            await pulse.ConnectAsync(ip, port, login.AuthChainJson, startup);
            var clock = new StopWatchTimeProvider();
            var encoder = new ParcelEncoder(-150, -150, 163, 2, 16);
            var bot = new BotSession
            {
                AccountName = account, WalletAddress = Wallet, Profile = new Profile(new Web3Address(Wallet), 1, []),
                Pipe = Pipe, Service = pulse, InputReader = new StationaryInput(), SpawnOrigin = position,
                Position = position, LastFrameTick = clock.TimeSinceStartupMs,
            };
            int parcel = encoder.EncodeGlobalPosition(position, out Vector3 relative);
            Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
            {
                Teleport = new TeleportRequest { Realm = realm, ParcelIndex = parcel, PositionXQuantized = relative.X,
                    PositionYQuantized = relative.Y, PositionZQuantized = relative.Z },
            }, PacketMode.RELIABLE));
            var loop = new SimulationLoop([bot], new ClientOptions(), new BotBehaviorSettings(), encoder, clock);
            pulseTasks = [Track(loop.RunAsync(life.Token), life.Token), Track(ServerEventHandler.ProcessAll(bot, life.Token), life.Token)];
        }

        public async Task StartCommsAsync(string url, CancellationToken startup)
        {
            commsLife = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
            CancellationToken token = commsLife.Token;
            connection = new WebSocketCommsConnection();
            await new ArchipelagoSignFlow(connection, authenticator, account, device).ConnectAsync(url, Wallet, startup);
            var changes = Channel.CreateUnbounded<IslandChange>();
            var listener = new ConnStringListener(connection);
            listener.IslandChanged += change => changes.Writer.TryWrite(change);
            commsTasks = [Track(listener.RunAsync(token), token),
                Track(new HeartbeatPump(connection, () => position, TimeSpan.FromSeconds(2)).RunAsync(token), token),
                Track(JoinAsync(changes.Reader, token), token)];
        }

        private async Task JoinAsync(ChannelReader<IslandChange> changes, CancellationToken token)
        {
            await foreach (IslandChange change in changes.ReadAllAsync(token))
            {
                Interlocked.Increment(ref assignmentCount);
                await Room.JoinAsync(change.ConnStr, change.IslandId, token);
            }
        }

        private async Task Track(Task task, CancellationToken token)
        {
            try { await task; }
            catch (Exception) when (token.IsCancellationRequested) { }
            catch (Exception e) { Interlocked.CompareExchange(ref failure, ConnStringRedaction.Redact(e.Message), null); }
        }

        public void Check()
        {
            Assert.That(Volatile.Read(ref failure), Is.Null, $"{device}: transport/comms failure");
            Assert.That(Room.Failure, Is.Null, $"{device}: LiveKit join failure");
            Assert.That(Pipe.LastDisconnectReason, Is.Null, $"{device}: active Pulse connection lost");
        }

        public async Task StopCommsAsync(CancellationToken cleanup)
        {
            if (commsLife is null) return;
            await commsLife.CancelAsync();
            var errors = new List<Exception>();
            try { await Task.WhenAll(commsTasks).WaitAsync(cleanup); }
            catch (Exception e) { errors.Add(e); }
            try { if (connection is not null) await connection.DisposeAsync(); }
            catch (Exception e) { errors.Add(e); }
            try { await Room.DisconnectAsync(cleanup); }
            catch (Exception e) { errors.Add(e); }
            finally
            {
                commsLife.Dispose();
                commsLife = null;
            }
            if (errors.Count > 0) throw new AggregateException($"{account}/{device}: comms cleanup failed", errors);
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await DisposeAsync(cleanup.Token);
        }

        public async ValueTask DisposeAsync(CancellationToken cleanup)
        {
            await life.CancelAsync();
            var errors = new List<Exception>();
            bool commsStopped = false;
            try
            {
                await StopCommsAsync(cleanup);
                commsStopped = true;
            }
            catch (Exception e) { errors.Add(e); }
            try { await Task.WhenAll(pulseTasks).WaitAsync(cleanup); }
            catch (Exception e) { errors.Add(e); }
            try
            {
                if (pulse is not null) await pulse.DisconnectAsync(DisconnectReason.GRACEFUL, cleanup);
            }
            catch (Exception e) { errors.Add(e); }
            finally { pulse?.Dispose(); }
            // A failed gate wait may leave a join running; do not dispose its semaphore underneath it.
            if (commsStopped)
            {
                try { await Room.DisposeAsync(); }
                catch (Exception e) { errors.Add(e); }
            }
            life.Dispose();
            if (errors.Count > 0) throw new AggregateException($"{account}/{device}: probe cleanup failed", errors);
        }
    }
}
