using Decentraland.Pulse;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using Pulse.Clusters;
using Pulse.Peers.Simulation;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

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
    private string brokerUrl = string.Empty;
    private string wallet = string.Empty;
    private NatsPublisher publisher;
    private NatsConnection client;
    private ClusterBoard board;
    private CancellationTokenSource deadline;

    [OneTimeSetUp]
    public void RequireBroker()
    {
        // Decided once for the fixture, so no per-test SetUp ever exits early and TearDown never
        // meets a half-built fixture.
        brokerUrl = Environment.GetEnvironmentVariable("NATS_TEST_URL") ?? string.Empty;
        if (brokerUrl.Trim().Length == 0)
            Assert.Ignore("Set NATS_TEST_URL to run the real-broker recovery integration tests.");
    }

    [SetUp]
    public async Task SetUp()
    {
        deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        wallet = RandomWallet();
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
            RequestTimeout = TimeSpan.FromMilliseconds(500),
        });
        await client.ConnectAsync();
        await publisher.StartAsync(deadline.Token);

        // StartAsync schedules the service; wait for the responder itself, not merely a TCP
        // connection. Another process may already have the wildcard subscription while this
        // owner is still starting, so both no-responders and silent misses are retried.
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
            catch (NatsNoReplyException) when (!deadline.IsCancellationRequested)
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
            await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            publisher.Dispose();
            await client.DisposeAsync();
            deadline.Dispose();
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
    public void Request_DifferentSession_TimesOutWithoutReplyingForAnotherOwner()
    {
        Assert.ThrowsAsync<NatsNoReplyException>(async () => await RequestBytesAsync(OTHER_SESSION));
    }

    [Test]
    public void Request_WithoutASession_GetsNoReply()
    {
        Assert.ThrowsAsync<NatsNoReplyException>(async () => await RequestBytesAsync(string.Empty));
    }

    [Test]
    public async Task Request_ChecksumCasedWalletSubject_ResolvesTheLowerCasedAssignment()
    {
        NatsMsg<byte[]> reply = await client.RequestAsync<byte[], byte[]>(
            $"peer.{wallet.ToUpperInvariant()}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION), cancellationToken: deadline.Token);

        Assert.That(PeerClusterChange.Parser.ParseFrom(reply.Data ?? []).ClusterId, Is.EqualTo("C1"));
    }

    [Test]
    public async Task Request_NamingAForeignReplySubject_IsNotAnsweredThere()
    {
        string foreignSubject = $"peer.{wallet}.cluster_change";
        await using INatsSub<byte[]> foreign = await client.SubscribeCoreAsync<byte[]>(foreignSubject, cancellationToken: deadline.Token);
        await client.PingAsync(deadline.Token);

        await client.PublishAsync($"peer.{wallet}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION),
            replyTo: foreignSubject, cancellationToken: deadline.Token);

        // The responder handles one subscription in order, and the broker delivers to one connection in
        // order, so once this well-formed request has been answered any reply to the forged one would
        // already be sitting on the foreign subscription.
        Assert.That((await RequestAssignmentAsync(SESSION)).ClusterId, Is.EqualTo("C1"));
        Assert.That(foreign.Msgs.TryRead(out _), Is.False, "a reply must never be published under a requester-chosen subject");
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task Request_WithAnotherPublisher_OnlyMatchingOwnerResponds(bool otherHasDifferentSession)
    {
        string probeWallet = RandomWallet();
        var otherBoard = new ClusterBoard();
        var assignments = new Dictionary<string, ClusterAssignment>
        {
            [probeWallet] = new("probe", "realm-probe", SESSION),
        };
        if (otherHasDifferentSession)
            assignments[wallet] = new("wrong-room", "other-realm", OTHER_SESSION);
        otherBoard.PublishAssignments(assignments);
        using NatsPublisher other = CreateAdditionalPublisher(otherBoard);
        await other.StartAsync(deadline.Token);
        try
        {
            // The first publisher has no probe assignment. Receiving this reply proves that
            // both subscriptions are active without a startup timing assumption.
            await WaitForAssignmentAsync(probeWallet, "the additional responder never became ready");

            for (var attempt = 0; attempt < 100; attempt++)
            {
                PeerClusterChange response = await RequestAssignmentAsync(SESSION);
                Assert.That(response.ClusterId, Is.EqualTo("C1"));
                Assert.That(response.Session, Is.EqualTo(SESSION));
            }
        }
        finally
        {
            await other.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Reply_WhenPayloadCannotBePublished_RecordsFailureAndKeepsResponding()
    {
        string probeWallet = RandomWallet();
        var otherBoard = new ClusterBoard();
        void SetRealm(string realm) => otherBoard.PublishAssignments(new Dictionary<string, ClusterAssignment>
        {
            [probeWallet] = new("probe", realm, SESSION),
        });
        SetRealm("small");
        using NatsPublisher other = CreateAdditionalPublisher(otherBoard);
        await other.StartAsync(deadline.Token);
        try
        {
            await WaitForAssignmentAsync(probeWallet, "the additional responder never became ready");
            Assert.That(() => other.PublishedCount, Is.GreaterThan(0).After(1000, 10));
            long publishedBeforeFailure = other.PublishedCount;
            // The test broker uses the default 1 MiB max_payload. Serialization succeeds,
            // but the real client rejects this response before it reaches the broker.
            SetRealm(new string('r', 2 * 1024 * 1024));
            Assert.ThrowsAsync<NatsNoReplyException>(async () => await client.RequestAsync<byte[], byte[]>(
                $"peer.{probeWallet}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION), cancellationToken: deadline.Token));
            Assert.That(() => other.PublishFailedCount, Is.EqualTo(1).After(1000, 10));
            Assert.That(other.PublishedCount, Is.EqualTo(publishedBeforeFailure));
            Assert.That(other.DroppedCount, Is.Zero);

            SetRealm("recovered");
            PeerClusterChange response = await WaitForAssignmentAsync(probeWallet, "the responder never answered after the failed reply");
            Assert.That(response.Realm, Is.EqualTo("recovered"));
            Assert.That(() => other.PublishedCount, Is.GreaterThan(publishedBeforeFailure).After(1000, 10));
        }
        finally
        {
            await other.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task ConnectionLoss_ResumesAuthorityAndCurrentHintsWithoutMovement()
    {
        string probeWallet = RandomWallet();
        var otherBoard = new ClusterBoard();
        void SetRoom(string room) => otherBoard.PublishAssignments(new Dictionary<string, ClusterAssignment>
        {
            [probeWallet] = new(room, "realm", SESSION),
        });
        SetRoom("before");
        await using var proxy = new BrokerProxy(new Uri(brokerUrl));
        using var other = new NatsPublisher(NullLogger<NatsPublisher>.Instance, NullLoggerFactory.Instance,
            Options.Create(new NatsOptions
            {
                Url = proxy.Url,
                DiscoveryIntervalMs = 0,
                AssignmentRefreshIntervalMs = 50,
            }), new SnapshotBoard(10, 4), otherBoard);
        await using INatsSub<byte[]> hints = await client.SubscribeCoreAsync<byte[]>(
            $"peer.{probeWallet}.cluster_snapshot", cancellationToken: deadline.Token);
        await client.PingAsync(deadline.Token);
        await other.StartAsync(deadline.Token);
        try
        {
            Assert.That((await WaitForAssignmentAsync(probeWallet, "the proxied responder never became ready")).ClusterId,
                Is.EqualTo("before"));
            proxy.Disconnect();
            while (other.ReconnectCount == 0)
                await Task.Delay(10, deadline.Token);

            // This value cannot exist in any hint queued before the outage; observing it
            // below proves the periodic loop is still publishing after reconnection.
            SetRoom("after");
            Assert.That((await WaitForAssignmentAsync(probeWallet, "the responder never answered after reconnecting")).ClusterId,
                Is.EqualTo("after"));
            while (true)
            {
                PeerClusterChange hint = PeerClusterChange.Parser.ParseFrom(
                    (await hints.Msgs.ReadAsync(deadline.Token)).Data ?? []);
                if (hint.ClusterId != "after") continue;
                Assert.That(hint.Session, Is.EqualTo(SESSION));
                break;
            }
        }
        finally
        {
            await other.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public void Request_AfterPeerDeparture_TimesOutRatherThanReturningRetainedAssignment()
    {
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>());

        Assert.ThrowsAsync<NatsNoReplyException>(async () => await RequestBytesAsync(SESSION));
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
    public async Task RecoveryHints_CountAsPublished()
    {
        string probeWallet = RandomWallet();
        var otherBoard = new ClusterBoard();
        otherBoard.PublishAssignments(new Dictionary<string, ClusterAssignment>
        {
            [probeWallet] = new("probe", "realm", SESSION),
        });
        await using INatsSub<byte[]> hints = await client.SubscribeCoreAsync<byte[]>(
            $"peer.{probeWallet}.cluster_snapshot", cancellationToken: deadline.Token);
        await client.PingAsync(deadline.Token);
        // Heartbeat off, empty outbox and no request sent to it: hints are all this publisher sends.
        using NatsPublisher other = CreateAdditionalPublisher(otherBoard, assignmentRefreshIntervalMs: 50);
        await other.StartAsync(deadline.Token);
        try
        {
            await hints.Msgs.ReadAsync(deadline.Token);
            await hints.Msgs.ReadAsync(deadline.Token);

            Assert.That(() => other.PublishedCount, Is.GreaterThanOrEqualTo(2).After(1000, 10));
            Assert.That(other.PublishFailedCount, Is.Zero);
        }
        finally
        {
            await other.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Stop_WithResponderAndPeriodicHintsRunning_CompletesWithinBound()
    {
        await publisher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(publisher.IsConnected, Is.False);
    }

    private void PublishAssignment(string clusterId, string realm)
    {
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>
        {
            [wallet] = new(clusterId, realm, SESSION),
        });
    }

    private static string RandomWallet() =>
        "0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    private NatsPublisher CreateAdditionalPublisher(ClusterBoard assignments, int assignmentRefreshIntervalMs = 0) =>
        new(NullLogger<NatsPublisher>.Instance, NullLoggerFactory.Instance,
            Options.Create(new NatsOptions
            {
                Url = brokerUrl,
                DiscoveryIntervalMs = 0,
                AssignmentRefreshIntervalMs = assignmentRefreshIntervalMs,
            }), new SnapshotBoard(10, 4), assignments);

    private async Task<PeerClusterChange> WaitForAssignmentAsync(string targetWallet, string timeoutMessage)
    {
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                NatsMsg<byte[]> reply = await client.RequestAsync<byte[], byte[]>(
                    $"peer.{targetWallet}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION), cancellationToken: deadline.Token);
                return PeerClusterChange.Parser.ParseFrom(reply.Data ?? []);
            }
            catch (NatsNoReplyException) when (!deadline.IsCancellationRequested) { }
            catch (NatsNoRespondersException) when (!deadline.IsCancellationRequested) { }
        }

        throw new TimeoutException(timeoutMessage);
    }

    private ValueTask<NatsMsg<byte[]>> RequestBytesAsync(string session) =>
        client.RequestAsync<byte[], byte[]>(
            $"peer.{wallet}.cluster_assignment", Encoding.UTF8.GetBytes(session), cancellationToken: deadline.Token);

    private async Task<PeerClusterChange> RequestAssignmentAsync(string session)
    {
        NatsMsg<byte[]> reply = await RequestBytesAsync(session);
        return PeerClusterChange.Parser.ParseFrom(reply.Data ?? []);
    }

    /// <summary>Severs only this test publisher's real broker connection; never restarts the shared broker.</summary>
    private sealed class BrokerProxy : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopped = new();
        private readonly ConcurrentBag<TcpClient> sockets = [];
        private readonly List<Task> forwards = [];
        private readonly Task accepting;
        private readonly Uri broker;

        public string Url { get; }

        public BrokerProxy(Uri broker)
        {
            this.broker = broker;
            listener.Start();
            Url = $"nats://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            accepting = AcceptAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await stopped.CancelAsync();
            await accepting;
            listener.Stop();
            Disconnect();
            await Task.WhenAll(forwards);
            stopped.Dispose();
        }

        public void Disconnect()
        {
            foreach (TcpClient socket in sockets) socket.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!stopped.IsCancellationRequested)
                {
                    TcpClient downstream = await listener.AcceptTcpClientAsync(stopped.Token);
                    sockets.Add(downstream);
                    forwards.Add(ForwardAsync(downstream));
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        }

        private async Task ForwardAsync(TcpClient downstream)
        {
            using (downstream)
            using (var upstream = new TcpClient())
            {
                sockets.Add(upstream);
                try
                {
                    await upstream.ConnectAsync(broker.Host, broker.Port, stopped.Token);
                    Task outbound = downstream.GetStream().CopyToAsync(upstream.GetStream(), stopped.Token);
                    Task inbound = upstream.GetStream().CopyToAsync(downstream.GetStream(), stopped.Token);
                    await Task.WhenAny(outbound, inbound);
                    downstream.Dispose();
                    upstream.Dispose();
                    await Task.WhenAll(outbound, inbound);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
            }
        }
    }
}
