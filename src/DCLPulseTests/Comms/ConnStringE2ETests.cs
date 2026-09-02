using PulseTestClient;
using PulseTestClient.Auth;
using PulseTestClient.Comms;
using System.Numerics;

namespace DCLPulseTests.Comms;

/// <summary>
///     Drives the test client's own comms types against a live ws-connector and asserts that a
///     LiveKit connection string comes back for the wallet that authenticated.
/// </summary>
/// <remarks>
///     <para>
///         Opt-in. It needs a ws-connector, an island producer behind it, and a <c>metaforge</c> on
///         PATH new enough to have <c>account sign</c>. Run with
///         <c>dotnet test --filter TestCategory=E2E</c>.
///     </para>
///     <para>
///         It defaults to the deployed zone realm, whose adapter is read from
///         <c>https://peer.decentraland.zone/about</c>. There the island is assigned from the
///         <b>heartbeat position</b> this fixture sends, so the comms channel alone is a complete
///         test. Once comms-gatekeeper takes over, assignment comes from Pulse's
///         <c>cluster_change</c> instead and heartbeats stop driving it — at that point this fixture
///         also needs a Pulse session to move the peer, or it will time out against a healthy stack.
///     </para>
///     <para>
///         Assertions deliberately key on <i>relationships</i> — a conn string arrived, two peers
///         share an island — never on how an island id is spelled. The deployed archipelago emits
///         <c>peer-zone1</c>; gatekeeper emits <c>island-{clusterId}</c>. A test that pins the
///         spelling passes on one and fails on the other while nothing is actually broken.
///     </para>
/// </remarks>
[TestFixture]
[Category("E2E")]
[Explicit("Needs a live ws-connector, an island producer, and a metaforge with 'account sign'.")]
public class ConnStringE2ETests
{
    // The realm's comms.adapter verbatim; AdapterAddress reduces it. Override to point at a local
    // stack, e.g. ws://127.0.0.1:5010/ws.
    private const string DEFAULT_COMMS_URL = "archipelago:archipelago:wss://peer.decentraland.zone/archipelago/ws";

    // Two accounts, not one: ws-connector allows a single session per wallet and kicks the previous
    // one with KR_NEW_SESSION, so a same-wallet pair would test the kick rather than the island.
    private const string DEFAULT_ACCOUNT_A = "loadtest-0";
    private const string DEFAULT_ACCOUNT_B = "loadtest-1";

    // Generous on purpose. Assignment is seconds away by design and the deadline exists only to fail
    // a hung run, so it is never the thing under test.
    private const int DEFAULT_TIMEOUT_SECONDS = 30;

    private static readonly Vector3 GENESIS_PLAZA = new (-104f, 0f, 5f);

    private static string CommsUrl => Env("PULSE_E2E_COMMS_URL", DEFAULT_COMMS_URL);
    private static string AccountA => Env("PULSE_E2E_ACCOUNT_A", DEFAULT_ACCOUNT_A);
    private static string AccountB => Env("PULSE_E2E_ACCOUNT_B", DEFAULT_ACCOUNT_B);

    private static TimeSpan Timeout =>
        TimeSpan.FromSeconds(int.TryParse(Env("PULSE_E2E_TIMEOUT_SECONDS", ""), out int s) ? s : DEFAULT_TIMEOUT_SECONDS);

    private static string Env(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    ///     Fails before any socket is opened if <c>metaforge</c> cannot sign an arbitrary payload.
    /// </summary>
    /// <remarks>
    ///     Without this the run gets as far as a real challenge from a real ws-connector and dies
    ///     there, which reads like a protocol fault. It is a stale binary: <c>account sign</c> is
    ///     newer than the released build, so a machine that has only ever installed MetaForge will
    ///     not have it.
    /// </remarks>
    [OneTimeSetUp]
    public async Task RequireAMetaForgeThatCanSign()
    {
        try
        {
            // --help exits 0 when the subcommand exists and 127 when it does not, so this probes the
            // surface without touching an account or producing a signature.
            await MetaForge.RunCommandAsync("account sign --help --skip-update-check", CancellationToken.None);
        }
        catch (Exception e)
        {
            Assert.Fail(
                "This fixture needs a 'metaforge' that supports 'account sign', which the released " +
                "build does not yet have. Build MetaForgeCLI and put its output directory first on " +
                $"PATH, then re-run.{Environment.NewLine}Probe failed with: {e.Message}");
        }
    }

    [Test]
    public async Task SingleBot_ReceivesAConnStringForItsOwnWallet()
    {
        using var cts = new CancellationTokenSource(Timeout * 2);

        Observation result = await ObserveAsync(AccountA, GENESIS_PLAZA, cts.Token);

        Assert.Multiple(() =>
        {
            // ws-connector derives the peer id from the auth chain, not from the address we claimed.
            // A mismatch here is why an otherwise healthy session never receives anything: the
            // registry and the NATS subject both key on the server's spelling.
            Assert.That(result.PeerId, Is.EqualTo(result.Wallet),
                "welcomed peer id must equal the lowercased wallet that signed");

            Assert.That(result.Island.IslandId, Is.Not.Empty, "island id must be populated");
            Assert.That(result.Island.ConnStr, Does.StartWith("livekit:"),
                "conn string must carry the livekit scheme");
            Assert.That(AccessTokenOf(result.Island.ConnStr), Is.Not.Empty,
                "conn string must carry a non-empty access_token");
        });
    }

    [Test]
    public async Task TwoBotsAtTheSamePosition_LandOnTheSameIsland()
    {
        using var cts = new CancellationTokenSource(Timeout * 2);

        // Concurrently, so both are present for whichever pass or heartbeat forms the island. Started
        // one after the other, the first can be assigned alone and only merged later.
        Task<Observation> a = ObserveAsync(AccountA, GENESIS_PLAZA, cts.Token);
        Task<Observation> b = ObserveAsync(AccountB, GENESIS_PLAZA, cts.Token);
        Observation[] both = await Task.WhenAll(a, b);

        Assert.That(both[0].Wallet, Is.Not.EqualTo(both[1].Wallet),
            "the two accounts must be different wallets, or this tests the same-wallet kick instead");

        // The assertion that survives the migration: same place, same island. Not what it is called.
        Assert.That(both[0].Island.IslandId, Is.EqualTo(both[1].Island.IslandId),
            "two peers at the same position belong in one island");
    }

    /// <summary>
    ///     Runs the handshake, pumps heartbeats, and resolves on the first island assignment.
    /// </summary>
    private static async Task<Observation> ObserveAsync(string account, Vector3 position, CancellationToken ct)
    {
        IAuthenticator authenticator = new MetaForgeAuthenticator();

        // Same call the bot lifecycle makes; it is also what tells us which wallet the account maps to.
        LoginResult login = await authenticator.LoginAsync(account, ct);
        string wallet = login.WalletAddress.ToLowerInvariant();

        await using var connection = new WebSocketCommsConnection();

        var signFlow = new ArchipelagoSignFlow(connection, authenticator, account);
        string peerId = await signFlow.ConnectAsync(CommsUrl, wallet, ct);

        var firstIsland = new TaskCompletionSource<IslandChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new ConnStringListener(connection);
        listener.IslandChanged += change => firstIsland.TrySetResult(change);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Both loops are faulted into the same completion source, so a kick or a dropped socket fails
        // the test with its own reason instead of as an unexplained timeout.
        Task listening = Fail(listener.RunAsync(stop.Token), firstIsland);

        // Two seconds, not the 30 s default: the first beat is what gives the producer a position, and
        // a second beat well inside the deadline covers one being missed.
        Task beating = Fail(new HeartbeatPump(connection, () => position, TimeSpan.FromSeconds(2)).RunAsync(stop.Token), firstIsland);

        try
        {
            Task finished = await Task.WhenAny(firstIsland.Task, Task.Delay(Timeout, ct));

            if (finished != firstIsland.Task)
                Assert.Fail($"[{account}] no island within {Timeout.TotalSeconds:0} s of the welcome. " +
                            "Nothing is producing island_changed for this wallet — check that an island producer " +
                            "is running against the same broker, and that it saw this exact address.");

            return new Observation(wallet, peerId, await firstIsland.Task);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(Swallow(listening), Swallow(beating));
        }
    }

    private static Task Fail(Task task, TaskCompletionSource<IslandChange> target) =>
        task.ContinueWith(t =>
            {
                if (t.Exception is { } e) target.TrySetException(e.GetBaseException());
            },
            TaskContinuationOptions.OnlyOnFaulted);

    private static Task Swallow(Task task) =>
        task.ContinueWith(_ => { }, TaskContinuationOptions.None);

    /// <summary>
    ///     The token, for emptiness checks only. Never assert on its value and never log it — with a
    ///     real producer it is a live credential.
    /// </summary>
    private static string AccessTokenOf(string connStr)
    {
        int at = connStr.IndexOf("access_token=", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return string.Empty;

        string tail = connStr[(at + "access_token=".Length)..];
        int end = tail.IndexOf('&');
        return end < 0 ? tail : tail[..end];
    }

    private readonly record struct Observation(string Wallet, string PeerId, IslandChange Island);
}
