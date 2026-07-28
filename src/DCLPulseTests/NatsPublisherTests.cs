using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.Clusters;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class NatsPublisherTests
{
    private const int MAX_PEERS = 100;
    private const int RING_CAPACITY = 4;

    private SnapshotBoard snapshotBoard;

    [SetUp]
    public void SetUp()
    {
        snapshotBoard = new SnapshotBoard(MAX_PEERS, RING_CAPACITY);
    }

    [Test]
    public void WhenUrlUnset_PublishingIsANoOp()
    {
        NatsPublisher publisher = CreatePublisher(url: string.Empty);

        publisher.PublishClusterChange("0xwallet0", "C1", "realm-a");
        publisher.PublishTopology(MakePass());

        Assert.That(publisher.PublishedCount, Is.Zero);
        Assert.That(publisher.DroppedCount, Is.Zero);
        Assert.That(publisher.IsConnected, Is.False);
    }

    [Test]
    public async Task WhenUrlUnset_TheServiceExitsWithoutConnecting()
    {
        NatsPublisher publisher = CreatePublisher(url: string.Empty);

        // Stats-only mode: starting must not attempt a connection nor throw.
        await publisher.StartAsync(CancellationToken.None);
        await publisher.StopAsync(CancellationToken.None);

        Assert.That(publisher.IsConnected, Is.False);
        Assert.That(publisher.PublishedCount, Is.Zero);
    }

    [Test]
    public void ChannelOverflow_DropsOldestAndCountsEveryDrop()
    {
        // Configured but never started, so nothing drains the queue and it fills deterministically.
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 2);

        for (var i = 0; i < 5; i++)
            publisher.PublishClusterChange($"0xwallet{i}", $"C{i}", "realm-a");

        // Capacity 2, five writes — the three oldest are evicted to make room.
        Assert.That(publisher.DroppedCount, Is.EqualTo(3));
        Assert.That(publisher.PublishedCount, Is.Zero);
    }

    [Test]
    public void WithinCapacity_NothingIsDropped()
    {
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 8);

        for (var i = 0; i < 8; i++)
            publisher.PublishClusterChange($"0xwallet{i}", $"C{i}", "realm-a");

        Assert.That(publisher.DroppedCount, Is.Zero);
    }

    [Test]
    public void TopologyPublish_IsQueuedWhenConfigured()
    {
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 4);

        publisher.PublishTopology(MakePass());

        // Queued, not yet delivered — nothing is draining.
        Assert.That(publisher.DroppedCount, Is.Zero);
        Assert.That(publisher.PublishedCount, Is.Zero);
    }

    private NatsPublisher CreatePublisher(string url, int channelCapacity = 1024)
    {
        var options = Substitute.For<IOptions<NatsOptions>>();

        options.Value.Returns(new NatsOptions
        {
            Url = url,
            SubjectPrefix = string.Empty,
            ServerName = "pulse-test",
            DiscoveryIntervalMs = 10_000,
            ChannelCapacity = channelCapacity,
        });

        return new NatsPublisher(NullLogger<NatsPublisher>.Instance, options, snapshotBoard);
    }

    private static ClusterPass MakePass()
    {
        var clusterIdByPeer = new string?[MAX_PEERS];
        clusterIdByPeer[0] = "C1";

        return new ClusterPass(
            [new ClusterInfo("C1", "realm-a", 1, new Vector3(10, 0, 10), 0f)],
            [new ClusterPeerInfo(new PeerIndex(0), "0xwallet0", "C1", "realm-a", new Vector3(10, 0, 10), 0)],
            clusterIdByPeer);
    }
}
