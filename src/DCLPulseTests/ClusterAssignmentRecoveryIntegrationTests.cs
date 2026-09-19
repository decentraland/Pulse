using Decentraland.Pulse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Pulse.Clusters;
using Pulse.Peers.Simulation;
using System.Security.Cryptography;
using System.Text;

namespace DCLPulseTests;

/// <summary>
/// Exercises the hosted publisher's request/reply and recovery-hint wire contract against a real
/// broker. Run locally with NATS_TEST_URL=nats://127.0.0.1:4222; CI supplies its own NATS service.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ClusterAssignmentRecoveryIntegrationTests
{
    private const string SESSION = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OTHER_SESSION = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private NatsPublisher publisher = null!;
    private NatsConnection client = null!;
    private ClusterBoard board = null!;
    private CancellationTokenSource deadline = null!;
    private string wallet = string.Empty;

    [SetUp]
    public async Task SetUp()
    {
        string? brokerUrl = Environment.GetEnvironmentVariable("NATS_TEST_URL");
        if (string.IsNullOrWhiteSpace(brokerUrl))
            Assert.Ignore("Set NATS_TEST_URL to run the real-broker recovery integration tests.");

        deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        wallet = "0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        board = new ClusterBoard();
        PublishAssignment("C1", "realm-a");
        publisher = new NatsPublisher(NullLogger<NatsPublisher>.Instance, NullLoggerFactory.Instance,
            Options.Create(new NatsOptions
            {
                Url = brokerUrl,
                ServerName = "recovery-integration-" + wallet,
                DiscoveryIntervalMs = 0,
                AssignmentRefreshIntervalMs = 50,
            }), new SnapshotBoard(10, 4), board);
        client = new NatsConnection(NatsOpts.Default with
        {
            Url = brokerUrl,
            Name = "recovery-test-client-" + wallet,
            RequestTimeout = TimeSpan.FromSeconds(2),
        });
        await client.ConnectAsync();
        await publisher.StartAsync(deadline.Token);

        // StartAsync schedules the service; wait for the responder itself, not merely a TCP
        // connection. Only startup's no-responders condition is retried, bounded by the deadline.
        while (true)
        {
            try
            {
                await RequestAssignmentAsync(SESSION);
                break;
            }
            catch (NatsNoRespondersException)
            {
                await Task.Delay(10, deadline.Token);
            }
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            if (publisher != null)
                await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            publisher?.Dispose();
            if (client != null) await client.DisposeAsync();
            deadline?.Dispose();
        }
    }

    [Test]
    public async Task Request_RawSessionBytes_ReturnsProtobufAssignment()
    {
        PeerClusterChange response = await RequestAssignmentAsync(SESSION);

        Assert.Multiple(() =>
        {
            Assert.That(response.ClusterId, Is.EqualTo("C1"));
            Assert.That(response.Realm, Is.EqualTo("realm-a"));
            Assert.That(response.Session, Is.EqualTo(SESSION));
            Assert.That(response.DisplacedSession, Is.Empty);
            Assert.That(response.DisplacedClusterId, Is.Empty);
        });
    }

    [Test]
    public async Task Request_DifferentSession_ReturnsEmptyProtobuf()
    {
        NatsMsg<byte[]> reply = await RequestBytesAsync(OTHER_SESSION);

        Assert.That(reply.Data ?? [], Is.Empty);
    }

    [Test]
    public async Task Request_AfterAssignmentChanges_ReturnsCurrentBoardValue()
    {
        PublishAssignment("C2", "realm-b");

        PeerClusterChange response = await RequestAssignmentAsync(SESSION);

        Assert.Multiple(() =>
        {
            Assert.That(response.ClusterId, Is.EqualTo("C2"));
            Assert.That(response.Realm, Is.EqualTo("realm-b"));
            Assert.That(response.Session, Is.EqualTo(SESSION));
        });
    }

    [Test]
    public async Task Request_AfterPeerDeparture_ReturnsEmptyRatherThanRetainedAssignment()
    {
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>());

        NatsMsg<byte[]> reply = await RequestBytesAsync(SESSION);

        Assert.That(reply.Data ?? [], Is.Empty);
    }

    [Test]
    public async Task RecoveryHints_RepeatUnchangedAssignmentWithoutReplayingTakeover()
    {
        await using INatsSub<byte[]> hints = await client.SubscribeCoreAsync<byte[]>(
            $"peer.{wallet}.cluster_snapshot", cancellationToken: deadline.Token);
        await using INatsSub<byte[]> changes = await client.SubscribeCoreAsync<byte[]>(
            $"peer.{wallet}.cluster_change", cancellationToken: deadline.Token);
        await client.PingAsync(deadline.Token);

        publisher.PublishClusterChange(wallet, "C1", "realm-a", new ClusterSession(SESSION, OTHER_SESSION, "C0"));
        PeerClusterChange takeover = PeerClusterChange.Parser.ParseFrom(
            (await changes.Msgs.ReadAsync(deadline.Token)).Data ?? []);
        PeerClusterChange first = PeerClusterChange.Parser.ParseFrom(
            (await hints.Msgs.ReadAsync(deadline.Token)).Data ?? []);
        PeerClusterChange second = PeerClusterChange.Parser.ParseFrom(
            (await hints.Msgs.ReadAsync(deadline.Token)).Data ?? []);

        Assert.Multiple(() =>
        {
            Assert.That(takeover.DisplacedSession, Is.EqualTo(OTHER_SESSION), "the original change carries the eviction");
            Assert.That(takeover.DisplacedClusterId, Is.EqualTo("C0"));
            Assert.That(first.ClusterId, Is.EqualTo("C1"));
            Assert.That(first.Realm, Is.EqualTo("realm-a"));
            Assert.That(first.Session, Is.EqualTo(SESSION));
            Assert.That(first.DisplacedSession, Is.Empty);
            Assert.That(first.DisplacedClusterId, Is.Empty);
            Assert.That(second, Is.EqualTo(first), "unchanged assignments must still produce recovery hints");
        });
    }

    [Test]
    public async Task Stop_WithResponderAndPeriodicHintsRunning_CompletesWithinBound()
    {
        await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(publisher.IsConnected, Is.False);
    }

    private void PublishAssignment(string clusterId, string realm)
    {
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>(StringComparer.OrdinalIgnoreCase)
        {
            [wallet] = new(clusterId, realm, SESSION),
        });
    }

    private ValueTask<NatsMsg<byte[]>> RequestBytesAsync(string session) =>
        client.RequestAsync<byte[], byte[]>(
            $"peer.{wallet}.cluster_assignment", Encoding.UTF8.GetBytes(session), cancellationToken: deadline.Token);

    private async Task<PeerClusterChange> RequestAssignmentAsync(string session)
    {
        NatsMsg<byte[]> reply = await RequestBytesAsync(session);
        return PeerClusterChange.Parser.ParseFrom(reply.Data ?? []);
    }
}
