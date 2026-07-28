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

        // Capacity 2, five distinct peers — the three longest-waiting are evicted to make room.
        Assert.That(publisher.DroppedCount, Is.EqualTo(3));
        Assert.That(publisher.PublishedCount, Is.Zero);
    }

    /// <summary>
    ///     Guards against a shared oldest-first queue, which would evict one peer's assignment to admit
    ///     another's and leave the evicted peer published under a stale cluster indefinitely.
    /// </summary>
    [Test]
    public void RepeatedChangeForSamePeer_IsSupersededNotDropped()
    {
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 1);

        publisher.PublishClusterChange("0xwallet0", "C1", "realm-a");
        publisher.PublishClusterChange("0xwallet0", "C2", "realm-a");
        publisher.PublishClusterChange("0xwallet0", "C3", "realm-a");

        Assert.That(publisher.DroppedCount, Is.Zero, "a peer's own newer assignment must not count as loss");
        Assert.That(publisher.SupersededCount, Is.EqualTo(2));

        // Only the newest survives, and it is the only thing pending.
        Assert.That(publisher.TryDequeueNext(out string subject, out _), Is.True);
        Assert.That(subject, Does.EndWith("0xwallet0.cluster_change"));
        Assert.That(publisher.TryDequeueNext(out _, out _), Is.False);
    }

    [Test]
    public void ManyPeers_EachKeepsItsOwnSlot()
    {
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 4);

        // Two rounds over the same four peers: coalescing per peer, so nothing is lost.
        for (var round = 0; round < 2; round++)
            for (var i = 0; i < 4; i++)
                publisher.PublishClusterChange($"0xwallet{i}", $"C{round}", "realm-a");

        Assert.That(publisher.DroppedCount, Is.Zero);
        Assert.That(publisher.SupersededCount, Is.EqualTo(4));
        Assert.That(DrainSubjects(publisher), Has.Count.EqualTo(4));
    }

    [Test]
    public void RepeatedTopology_CoalescesIntoOnePendingSnapshot()
    {
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 4);

        publisher.PublishTopology(MakePass());
        publisher.PublishTopology(MakePass());
        publisher.PublishTopology(MakePass());

        Assert.That(publisher.DroppedCount, Is.Zero);
        Assert.That(publisher.SupersededCount, Is.EqualTo(2));
        Assert.That(DrainSubjects(publisher), Is.EqualTo(new[] { "engine.islands" }));
    }

    [Test]
    public void Topology_NeverEvictsAPeerAssignment()
    {
        // Capacity is fully consumed by peer assignments; topology must not compete for it.
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 2);

        publisher.PublishClusterChange("0xwallet0", "C1", "realm-a");
        publisher.PublishClusterChange("0xwallet1", "C1", "realm-a");

        for (var i = 0; i < 20; i++)
            publisher.PublishTopology(MakePass());

        Assert.That(publisher.DroppedCount, Is.Zero, "topology must never displace an assignment");

        List<string> drained = DrainSubjects(publisher);
        Assert.That(drained, Has.Count.EqualTo(3));
        Assert.That(drained, Has.Member("engine.islands"));
        Assert.That(drained.Count(s => s.EndsWith("cluster_change", StringComparison.Ordinal)), Is.EqualTo(2));
    }

    [Test]
    public void Topology_IsDeliveredBeforeTheChangesThatReferenceIt()
    {
        NatsPublisher publisher = CreatePublisher(url: "nats://localhost:4222", channelCapacity: 4);

        // Enqueued changes-first on purpose: the outbox, not the call order, must decide delivery.
        publisher.PublishClusterChange("0xwallet0", "C1", "realm-a");
        publisher.PublishTopology(MakePass());

        Assert.That(DrainSubjects(publisher)[0], Is.EqualTo("engine.islands"));
    }

    private static List<string> DrainSubjects(NatsPublisher publisher)
    {
        var subjects = new List<string>();

        while (publisher.TryDequeueNext(out string subject, out _))
            subjects.Add(subject);

        return subjects;
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
