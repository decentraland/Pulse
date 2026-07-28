using Decentraland.Kernel.Comms.V3;
using Decentraland.Pulse;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NSubstitute;
using NSubstitute.Core;
using Pulse.Clusters;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Buffers;
using System.Diagnostics.Metrics;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class NatsPublisherTests
{
    private const int MAX_PEERS = 100;
    private const int RING_CAPACITY = 4;

    private const string BROKER_URL = "nats://localhost:4222";
    private const string REALM = "realm-a";

    // Mirrors NatsPublisher's own placeholder so a change to the wording has to be made deliberately.
    private const string UNPARSED_BROKER_URL = "(unparsed broker url)";

    // Log lines the run loop emits, used as start barriers: StartAsync only schedules ExecuteAsync, so
    // without waiting for one of these a test can cancel the service before its body ever ran.
    private const string DISABLED_LOG = "NATS feed disabled";
    private const string STARTED_LOG = "NATS publisher started";

    private const int START_TIMEOUT_MS = 10_000;
    private const int START_POLL_MS = 5;

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
        var logger = Substitute.For<ILogger<NatsPublisher>>();
        NatsPublisher publisher = CreatePublisher(url: string.Empty, logger: logger);

        // Stats-only mode: starting must not attempt a connection nor throw.
        await publisher.StartAsync(CancellationToken.None);
        WaitForLog(logger, DISABLED_LOG);
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

        // Eviction is FIFO, so the survivors are the two most recent peers — and there are exactly
        // capacity-many of them, so the outbox never grew past its bound.
        Assert.That(DrainSubjects(publisher), Is.EqualTo(new[]
        {
            "peer.0xwallet3.cluster_change",
            "peer.0xwallet4.cluster_change",
        }));
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

    [Test]
    public void PublishTopology_ProjectsEveryClusterOntoAnIsland()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        ClusterPass pass = MakePass(
            [
                new ClusterInfo("C1", REALM, 2, new Vector3(10, 1, 20), 5f),
                new ClusterInfo("C2", REALM, 1, new Vector3(-30, 2, 40), 0f),
            ],
            [
                new ClusterPeerInfo(new PeerIndex(0), "0xwallet0", "C1", REALM, new Vector3(8, 0, 18), 0),
                new ClusterPeerInfo(new PeerIndex(1), "0xwallet1", "C1", REALM, new Vector3(12, 0, 22), 0),
                new ClusterPeerInfo(new PeerIndex(2), "0xwallet2", "C2", REALM, new Vector3(-30, 0, 40), 0),
            ]);

        publisher.PublishTopology(pass);

        (string subject, IMessage pending) = DequeueNext(publisher);
        IslandStatusMessage message = RoundTrip(IslandStatusMessage.Parser, pending);

        Assert.That(subject, Is.EqualTo("engine.islands"));
        Assert.That(message.Data.Select(island => island.Id), Is.EqualTo(new[] { "C1", "C2" }));

        IslandData first = message.Data[0];
        Assert.That(first.Radius, Is.EqualTo(5.0).Within(0.001));
        Assert.That(first.Center.X, Is.EqualTo(10f).Within(0.001f));
        Assert.That(first.Center.Y, Is.EqualTo(1f).Within(0.001f));
        Assert.That(first.Center.Z, Is.EqualTo(20f).Within(0.001f));
        Assert.That(first.Peers, Is.EqualTo(new[] { "0xwallet0", "0xwallet1" }));

        IslandData second = message.Data[1];
        Assert.That(second.Radius, Is.EqualTo(0.0).Within(0.001));
        Assert.That(second.Center.X, Is.EqualTo(-30f).Within(0.001f));
        Assert.That(second.Center.Y, Is.EqualTo(2f).Within(0.001f));
        Assert.That(second.Center.Z, Is.EqualTo(40f).Within(0.001f));
        Assert.That(second.Peers, Is.EqualTo(new[] { "0xwallet2" }));
    }

    /// <summary>
    ///     Clusters are uncapped by design, so the projection advertises zero rather than a bound a
    ///     subscriber could enforce.
    /// </summary>
    [Test]
    public void PublishTopology_AdvertisesNoPeerCapOnEveryIsland()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        ClusterPass pass = MakePass(
            [
                new ClusterInfo("C1", REALM, 1, new Vector3(10, 0, 10), 0f),
                new ClusterInfo("C2", REALM, 1, new Vector3(400, 0, 400), 0f),
            ],
            [
                new ClusterPeerInfo(new PeerIndex(0), "0xwallet0", "C1", REALM, new Vector3(10, 0, 10), 0),
                new ClusterPeerInfo(new PeerIndex(1), "0xwallet1", "C2", REALM, new Vector3(400, 0, 400), 0),
            ]);

        publisher.PublishTopology(pass);

        IslandStatusMessage message = RoundTrip(IslandStatusMessage.Parser, DequeueNext(publisher).Message);

        Assert.That(message.Data, Has.Count.EqualTo(2));

        foreach (IslandData island in message.Data)
            Assert.That(island.MaxPeers, Is.Zero, "zero advertises no cap; any other value implies a bound");
    }

    /// <summary>
    ///     A peer may name a cluster the same pass never declared. It is left out of the snapshot rather
    ///     than given an island of its own.
    /// </summary>
    [Test]
    public void PublishTopology_SkipsAPeerWhoseClusterIsNotInThePass()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        ClusterPass pass = MakePass(
            [new ClusterInfo("C1", REALM, 1, new Vector3(10, 0, 10), 0f)],
            [
                new ClusterPeerInfo(new PeerIndex(0), "0xwallet0", "C1", REALM, new Vector3(10, 0, 10), 0),
                new ClusterPeerInfo(new PeerIndex(1), "0xwallet1", "C9", REALM, new Vector3(10, 0, 10), 0),
            ]);

        publisher.PublishTopology(pass);

        IslandStatusMessage message = RoundTrip(IslandStatusMessage.Parser, DequeueNext(publisher).Message);

        Assert.That(message.Data, Has.Count.EqualTo(1));
        Assert.That(message.Data[0].Id, Is.EqualTo("C1"));
        Assert.That(message.Data[0].Peers, Is.EqualTo(new[] { "0xwallet0" }));
    }

    [Test]
    public void PublishTopology_WithNoClusters_PublishesAnEmptySnapshot()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        publisher.PublishTopology(ClusterPass.EMPTY);

        IslandStatusMessage message = RoundTrip(IslandStatusMessage.Parser, DequeueNext(publisher).Message);

        Assert.That(message.Data, Is.Empty);
    }

    /// <summary>
    ///     The snapshot instance, its islands and their centers are reused between passes, so every field
    ///     a pass does not set again would otherwise still carry the previous pass's value.
    /// </summary>
    [Test]
    public void PublishTopology_AfterALargerPass_CarriesNothingOverFromIt()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        publisher.PublishTopology(MakePass(
            [
                new ClusterInfo("C1", REALM, 2, new Vector3(10, 1, 20), 5f),
                new ClusterInfo("C2", REALM, 1, new Vector3(-30, 2, 40), 7f),
            ],
            [
                new ClusterPeerInfo(new PeerIndex(0), "0xwallet0", "C1", REALM, new Vector3(10, 0, 20), 0),
                new ClusterPeerInfo(new PeerIndex(1), "0xwallet1", "C1", REALM, new Vector3(11, 0, 21), 0),
                new ClusterPeerInfo(new PeerIndex(2), "0xwallet2", "C2", REALM, new Vector3(-30, 0, 40), 0),
            ]));

        // Taken out of the way so the next snapshot is published rather than superseding this one.
        DrainSubjects(publisher);

        // Fewer clusters, fewer members, and different geometry for the island both passes declare.
        publisher.PublishTopology(MakePass(
            [new ClusterInfo("C1", REALM, 1, new Vector3(1, 2, 3), 4f)],
            [new ClusterPeerInfo(new PeerIndex(3), "0xwallet3", "C1", REALM, new Vector3(1, 0, 3), 0)]));

        IslandStatusMessage message = RoundTrip(IslandStatusMessage.Parser, DequeueNext(publisher).Message);

        Assert.That(message.Data, Has.Count.EqualTo(1), "the island that vanished must not linger");

        IslandData island = message.Data[0];
        Assert.That(island.Id, Is.EqualTo("C1"));
        Assert.That(island.Radius, Is.EqualTo(4.0).Within(0.001));
        Assert.That(island.Center.X, Is.EqualTo(1f).Within(0.001f));
        Assert.That(island.Center.Y, Is.EqualTo(2f).Within(0.001f));
        Assert.That(island.Center.Z, Is.EqualTo(3f).Within(0.001f));
        Assert.That(island.Peers, Is.EqualTo(new[] { "0xwallet3" }), "the earlier members must be gone");
    }

    [Test]
    public void PublishClusterChange_RoundTripsClusterIdAndRealm()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        publisher.PublishClusterChange("0xwallet0", "C7", "realm-b");

        (string subject, IMessage pending) = DequeueNext(publisher);
        PeerClusterChange message = RoundTrip(PeerClusterChange.Parser, pending);

        Assert.That(subject, Is.EqualTo("peer.0xwallet0.cluster_change"));
        Assert.That(message.ClusterId, Is.EqualTo("C7"));
        Assert.That(message.Realm, Is.EqualTo("realm-b"));
    }

    /// <summary>
    ///     The outbox holds live messages drawn from a free list, so each pending peer must be holding an
    ///     instance of its own. A single shared instance would leave both entries pointing at the last
    ///     assignment written and publish the same cluster twice.
    /// </summary>
    [Test]
    public void TwoPendingChanges_CarryEachPeersOwnClusterId()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, channelCapacity: 4);

        publisher.PublishClusterChange("0xwallet0", "C1", "realm-a");
        publisher.PublishClusterChange("0xwallet1", "C2", "realm-b");

        (string firstSubject, IMessage firstPending) = DequeueNext(publisher);
        (string secondSubject, IMessage secondPending) = DequeueNext(publisher);

        Assert.That(firstPending, Is.Not.SameAs(secondPending), "two pending peers cannot share one instance");

        PeerClusterChange first = RoundTrip(PeerClusterChange.Parser, firstPending);
        PeerClusterChange second = RoundTrip(PeerClusterChange.Parser, secondPending);

        Assert.That(firstSubject, Is.EqualTo("peer.0xwallet0.cluster_change"));
        Assert.That(first.ClusterId, Is.EqualTo("C1"));
        Assert.That(first.Realm, Is.EqualTo("realm-a"));

        Assert.That(secondSubject, Is.EqualTo("peer.0xwallet1.cluster_change"));
        Assert.That(second.ClusterId, Is.EqualTo("C2"));
        Assert.That(second.Realm, Is.EqualTo("realm-b"));
    }

    /// <summary>
    ///     The client hands the serializer a buffer it owns and may make longer than the message, so what
    ///     the serializer advances is exactly what goes on the wire. Anything past the encoded message
    ///     would be appended to the payload.
    /// </summary>
    [Test]
    public void Serializer_WritesTheEncodedMessageAndNothingMore()
    {
        var message = new PeerClusterChange { ClusterId = "C7", Realm = "realm-b" };

        // Far longer than the message, which is what makes the advanced count load-bearing.
        var writer = new ArrayBufferWriter<byte>(1024);

        NatsPublisher.SERIALIZER.Serialize(writer, message);

        Assert.That(writer.WrittenCount, Is.EqualTo(message.CalculateSize()));
        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(message.ToByteArray()));
    }

    /// <summary>
    ///     A replaced assignment's instance has to go back to the free list. Nothing else notices if it
    ///     does not — the feed keeps working while every superseded change strands one message object.
    /// </summary>
    [Test]
    public void SupersededChange_ReturnsTheReplacedInstance()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, channelCapacity: 4);

        publisher.PublishClusterChange("0xwallet0", "C0", REALM);
        IMessage seeded = SeedFreeList(publisher);

        // The first change rents the seeded instance; the second rents a fresh one and must hand the
        // replaced instance back.
        publisher.PublishClusterChange("0xwallet0", "C1", REALM);
        publisher.PublishClusterChange("0xwallet0", "C2", REALM);

        // Only reuse of the replaced instance can satisfy this rent without creating another.
        publisher.PublishClusterChange("0xwallet1", "C3", REALM);

        Assert.That(publisher.SupersededCount, Is.EqualTo(1));

        // Arrival order: the surviving change for the first peer, then the second peer's.
        Assert.That(DequeueNext(publisher).Message, Is.Not.SameAs(seeded));
        Assert.That(DequeueNext(publisher).Message, Is.SameAs(seeded),
            "the replaced instance must be back on the free list");
    }

    /// <summary>
    ///     Eviction is the other path that takes a message out of the outbox without publishing it, and
    ///     the only one that also counts as loss.
    /// </summary>
    [Test]
    public void EvictedChange_ReturnsItsInstance()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, channelCapacity: 1);

        publisher.PublishClusterChange("0xwallet0", "C0", REALM);
        IMessage seeded = SeedFreeList(publisher);

        // Capacity one, so each new peer evicts the one before it and must hand its instance back.
        publisher.PublishClusterChange("0xwallet0", "C1", REALM);
        publisher.PublishClusterChange("0xwallet1", "C2", REALM);
        publisher.PublishClusterChange("0xwallet2", "C3", REALM);

        Assert.That(publisher.DroppedCount, Is.EqualTo(2));
        Assert.That(DequeueNext(publisher).Message, Is.SameAs(seeded),
            "the evicted instance must be back on the free list");
    }

    /// <summary>
    ///     A change instance handed back to the free list is filled again for whichever peer rents it
    ///     next, so a field the second peer's publish does not set again would go out carrying the
    ///     first peer's value.
    /// </summary>
    [Test]
    public void RecycledChange_CarriesNothingOverFromItsPreviousPeer()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, channelCapacity: 4);

        publisher.PublishClusterChange("0xwallet0", "C1", "realm-a");
        IMessage seeded = SeedFreeList(publisher);

        publisher.PublishClusterChange("0xwallet1", "C2", "realm-b");

        (string subject, IMessage pending) = DequeueNext(publisher);

        Assert.That(pending, Is.SameAs(seeded), "the second peer must be publishing on the recycled instance");

        PeerClusterChange message = RoundTrip(PeerClusterChange.Parser, pending);

        Assert.That(subject, Is.EqualTo("peer.0xwallet1.cluster_change"));
        Assert.That(message.ClusterId, Is.EqualTo("C2"));
        Assert.That(message.Realm, Is.EqualTo("realm-b"), "the previous peer's realm must be gone");
    }

    [Test]
    public void SupersededTopology_ReturnsTheReplacedInstance()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        publisher.PublishTopology(MakePass());
        IMessage seeded = SeedFreeList(publisher);

        publisher.PublishTopology(MakePass());
        publisher.PublishTopology(MakePass());
        publisher.PublishTopology(MakePass());

        Assert.That(publisher.SupersededCount, Is.EqualTo(2));
        Assert.That(DequeueNext(publisher).Message, Is.SameAs(seeded),
            "the replaced snapshot must be back on the free list");
    }

    /// <summary>
    ///     Shutdown is the last return path: a message the drain loop never reached is still checked out
    ///     of the free list, and after this nothing else will ever hand it back.
    /// </summary>
    [Test]
    public void Dispose_ReturnsEveryStillPendingInstance()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, channelCapacity: 4);

        publisher.PublishClusterChange("0xwallet0", "C0", REALM);
        IMessage seeded = SeedFreeList(publisher);

        publisher.PublishClusterChange("0xwallet0", "C1", REALM);
        publisher.Dispose();

        Assert.That(publisher.TryDequeueNext(out _, out _), Is.False, "the outbox is emptied as it is returned");

        publisher.PublishClusterChange("0xwallet1", "C2", REALM);

        Assert.That(DequeueNext(publisher).Message, Is.SameAs(seeded),
            "an instance still pending at shutdown must be returned");
    }

    /// <summary>
    ///     One wallet must map to one subject whatever checksum casing the auth chain carried — and since
    ///     the subject is the coalescing key, the two spellings must also coalesce into a single pending
    ///     entry rather than racing each other on the broker.
    /// </summary>
    [Test]
    public void PublishClusterChange_LowerCasesTheWalletSubject()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, channelCapacity: 4);

        publisher.PublishClusterChange("0xWALLET0", "C1", REALM);
        publisher.PublishClusterChange("0xwallet0", "C2", REALM);

        Assert.That(publisher.SupersededCount, Is.EqualTo(1), "the two spellings are the same peer");
        Assert.That(publisher.DroppedCount, Is.Zero);

        (string subject, IMessage pending) = DequeueNext(publisher);

        Assert.That(subject, Is.EqualTo("peer.0xwallet0.cluster_change"));
        Assert.That(RoundTrip(PeerClusterChange.Parser, pending).ClusterId, Is.EqualTo("C2"));
        Assert.That(publisher.TryDequeueNext(out _, out _), Is.False, "only one entry was ever pending");
    }

    [Test]
    public void SubjectPrefix_PrefixesTheClusterChangeSubject()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, subjectPrefix: "pulse-dev.");

        publisher.PublishClusterChange("0xwallet0", "C1", REALM);

        Assert.That(DequeueSubject(publisher), Is.EqualTo("pulse-dev.peer.0xwallet0.cluster_change"));
    }

    [Test]
    public void SubjectPrefix_PrefixesTheTopologySubject()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, subjectPrefix: "pulse-dev.");

        publisher.PublishTopology(MakePass());

        Assert.That(DequeueSubject(publisher), Is.EqualTo("pulse-dev.engine.islands"));
    }

    [Test]
    public async Task FirstConnectionOpen_IsNotCountedAsAReconnect()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        await publisher.OnConnectionOpened(null, ConnectionEvent());

        Assert.That(publisher.IsConnected, Is.True);
        Assert.That(publisher.ReconnectCount, Is.Zero, "the initial connect is not a reconnect");
    }

    [Test]
    public async Task ReopenAfterALostConnection_CountsAsAReconnect()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        await publisher.OnConnectionOpened(null, ConnectionEvent());
        await publisher.OnConnectionDisconnected(null, ConnectionEvent());
        await publisher.OnConnectionOpened(null, ConnectionEvent());

        Assert.That(publisher.ReconnectCount, Is.EqualTo(1));
        Assert.That(publisher.IsConnected, Is.True);
    }

    [Test]
    public async Task ConnectionEdges_ToggleIsConnected()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        Assert.That(publisher.IsConnected, Is.False, "configured is not connected");

        await publisher.OnConnectionOpened(null, ConnectionEvent());
        Assert.That(publisher.IsConnected, Is.True);

        await publisher.OnConnectionDisconnected(null, ConnectionEvent());
        Assert.That(publisher.IsConnected, Is.False);

        await publisher.OnConnectionOpened(null, ConnectionEvent());
        Assert.That(publisher.IsConnected, Is.True);
    }

    [Test]
    public async Task RepeatedOpensWithoutADisconnect_AddToTheConnectedGaugeOnlyOnce()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        using var gauge = new ConnectedGaugeProbe();

        await publisher.OnConnectionOpened(null, ConnectionEvent());
        await publisher.OnConnectionOpened(null, ConnectionEvent());
        await publisher.OnConnectionOpened(null, ConnectionEvent());

        Assert.That(gauge.Net, Is.EqualTo(1), "the gauge is a flag, not a count of opens");
        Assert.That(publisher.IsConnected, Is.True);
    }

    [Test]
    public async Task RepeatedDisconnects_SubtractFromTheConnectedGaugeOnlyOnce()
    {
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL);

        using var gauge = new ConnectedGaugeProbe();

        await publisher.OnConnectionOpened(null, ConnectionEvent());
        await publisher.OnConnectionDisconnected(null, ConnectionEvent());
        await publisher.OnConnectionDisconnected(null, ConnectionEvent());

        Assert.That(gauge.Net, Is.Zero, "a repeat of an edge already taken must add nothing");
        Assert.That(publisher.IsConnected, Is.False);
    }

    /// <summary>
    ///     The exported gauge accumulates deltas, so a +1 that shuts down without its -1 is a permanent
    ///     lie about a connection nobody holds.
    /// </summary>
    [Test]
    public async Task ShutdownAfterConnecting_LeavesTheConnectedGaugeBalanced()
    {
        // Heartbeat off so the run never reaches for a broker; the connection edge is signalled directly.
        var logger = Substitute.For<ILogger<NatsPublisher>>();
        NatsPublisher publisher = CreatePublisher(url: BROKER_URL, discoveryIntervalMs: 0, logger: logger);

        using var gauge = new ConnectedGaugeProbe();

        await publisher.StartAsync(CancellationToken.None);

        // The -1 under test comes from the run loop's own teardown, so it can only be observed once the
        // loop is actually inside the body that owns it.
        WaitForLog(logger, STARTED_LOG);

        await publisher.OnConnectionOpened(null, ConnectionEvent());
        Assert.That(gauge.Net, Is.EqualTo(1));

        await publisher.StopAsync(CancellationToken.None);

        Assert.That(gauge.Net, Is.Zero);
        Assert.That(publisher.IsConnected, Is.False);
    }

    /// <summary>
    ///     A non-positive interval is not a valid timer period. The heartbeat is skipped rather than
    ///     allowed to abort the publisher, so the outbox drain still starts and shutdown stays clean.
    /// </summary>
    [TestCase(0)]
    [TestCase(-1)]
    public async Task NonPositiveDiscoveryInterval_StartsAndStopsWithoutThrowing(int discoveryIntervalMs)
    {
        var logger = Substitute.For<ILogger<NatsPublisher>>();

        NatsPublisher publisher = CreatePublisher(
            url: BROKER_URL,
            discoveryIntervalMs: discoveryIntervalMs,
            logger: logger);

        await publisher.StartAsync(CancellationToken.None);

        // Reaching the startup line is the claim: the interval decision is made before it, so a throwing
        // timer would abort the run loop instead.
        WaitForLog(logger, STARTED_LOG);

        await publisher.StopAsync(CancellationToken.None);

        Assert.That(publisher.PublishedCount, Is.Zero, "no heartbeat means nothing was published");
        Assert.That(publisher.IsConnected, Is.False);
    }

    [TestCase("nats://broker.example:4222", "broker.example:4222")]
    [TestCase("nats://broker.example", "broker.example")]
    [TestCase("nats://fakeuser:fakepassword@broker.example:4222", "broker.example:4222")]
    [TestCase("nats://faketoken@broker.example", "broker.example")]
    [TestCase("nats://a.example:4222,nats://b.example:4223", "a.example:4222, b.example:4223")]
    [TestCase("  nats://a.example:4222 , nats://b.example:4223  ", "a.example:4222, b.example:4223")]
    [TestCase("nats://a.example:4222,,nats://b.example:4223,", "a.example:4222, b.example:4223")]
    [TestCase("nats://a.example:4222,not-a-url", "a.example:4222, " + UNPARSED_BROKER_URL)]
    [TestCase("not-a-url", UNPARSED_BROKER_URL)]
    [TestCase("", UNPARSED_BROKER_URL)]
    [TestCase("   ", UNPARSED_BROKER_URL)]
    [TestCase(",,,", UNPARSED_BROKER_URL)]
    public void SanitizeBrokerUrl_ReducesEveryEntryToHostAndPort(string url, string expected)
    {
        Assert.That(NatsPublisher.SanitizeBrokerUrl(url), Is.EqualTo(expected));
    }

    /// <summary>
    ///     The broker address is an injected secret and whatever the sanitizer returns is logged, so
    ///     userinfo must never survive it in any of the forms a NATS address can carry it.
    /// </summary>
    [TestCase("nats://fakeuser:fakepassword@broker.example:4222")]
    [TestCase("nats://fakeuser:fakepassword@broker.example")]
    [TestCase("nats://faketoken@broker.example:4222")]
    [TestCase("nats://fakeuser:fakepassword@a.example:4222,nats://faketoken@b.example:4223")]
    [TestCase("nats://faketoken@a.example:4222,not-a-url")]
    public void SanitizeBrokerUrl_NeverEmitsUserinfo(string url)
    {
        string sanitized = NatsPublisher.SanitizeBrokerUrl(url);

        // Every credential above is spelled "fake…", so one check covers user, password and token alike.
        Assert.That(sanitized, Does.Not.Contain("fake"));
        Assert.That(sanitized, Does.Not.Contain("@"));
    }

    private NatsPublisher CreatePublisher(
        string url,
        int channelCapacity = 1024,
        string subjectPrefix = "",
        int discoveryIntervalMs = 10_000,
        ILogger<NatsPublisher>? logger = null)
    {
        var options = Substitute.For<IOptions<NatsOptions>>();

        options.Value.Returns(new NatsOptions
        {
            Url = url,
            SubjectPrefix = subjectPrefix,
            ServerName = "pulse-test",
            DiscoveryIntervalMs = discoveryIntervalMs,
            ChannelCapacity = channelCapacity,
        });

        return new NatsPublisher(logger ?? NullLogger<NatsPublisher>.Instance, options, snapshotBoard);
    }

    /// <summary>
    ///     Blocks until the run loop has logged <paramref name="fragment" />. <c>StartAsync</c> only
    ///     schedules <c>ExecuteAsync</c> on this runtime, so a test that cancels straight after starting
    ///     can shut the service down before its body ever ran — and then observes none of what that body
    ///     owns.
    /// </summary>
    private static void WaitForLog(ILogger<NatsPublisher> logger, string fragment)
    {
        Assert.That(() => HasLogged(logger, fragment), Is.True.After(START_TIMEOUT_MS, START_POLL_MS),
            $"the publisher never logged \"{fragment}\"");
    }

    private static bool HasLogged(ILogger<NatsPublisher> logger, string fragment)
    {
        foreach (ICall call in logger.ReceivedCalls())
        {
            object?[] arguments = call.GetArguments();

            // Log's third argument is the state, whose ToString is the formatted message.
            if (arguments.Length < 3) continue;

            if (arguments[2]?.ToString() is { } message && message.Contains(fragment, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static ClusterPass MakePass()
    {
        return MakePass(
            [new ClusterInfo("C1", "realm-a", 1, new Vector3(10, 0, 10), 0f)],
            [new ClusterPeerInfo(new PeerIndex(0), "0xwallet0", "C1", "realm-a", new Vector3(10, 0, 10), 0)]);
    }

    private static ClusterPass MakePass(
        IReadOnlyList<ClusterInfo> clusters,
        IReadOnlyList<ClusterPeerInfo> peers)
    {
        var clusterIdByPeer = new string?[MAX_PEERS];

        for (var i = 0; i < peers.Count; i++)
            clusterIdByPeer[(int)peers[i].Peer.Value] = peers[i].ClusterId;

        return new ClusterPass(clusters, peers, clusterIdByPeer);
    }

    /// <summary>
    ///     Drains the outbox, returning each message the way the drain loop would so the instances go
    ///     back to their free lists instead of being stranded.
    /// </summary>
    private static List<string> DrainSubjects(NatsPublisher publisher)
    {
        var subjects = new List<string>();

        while (publisher.TryDequeueNext(out string subject, out IMessage? message))
        {
            subjects.Add(subject);
            publisher.Return(message);
        }

        return subjects;
    }

    /// <summary>
    ///     Takes the one message the outbox is expected to hand back next, so a test can assert both the
    ///     subject it was addressed to and the message that was built for it. The instance is returned the
    ///     way the drain loop would, leaving the free lists as the publisher itself would leave them;
    ///     reading it afterwards is safe because a returned instance is only rewritten once it is rented
    ///     again.
    /// </summary>
    private static (string Subject, IMessage Message) DequeueNext(NatsPublisher publisher)
    {
        Assert.That(publisher.TryDequeueNext(out string subject, out IMessage? message), Is.True,
            "expected a pending message");

        // The assertion above has already failed the test if nothing was pending. Restated here in a
        // form the compiler can follow, rather than forgiving the null.
        IMessage pending = message
                           ?? throw new InvalidOperationException("the outbox reported a message but handed back none");

        publisher.Return(pending);

        return (subject, pending);
    }

    /// <summary>
    ///     Takes the next message's subject, returning its instance the way the drain loop would.
    /// </summary>
    private static string DequeueSubject(NatsPublisher publisher) =>
        DequeueNext(publisher).Subject;

    /// <summary>
    ///     Encodes a message through the publisher's own serializer and parses the bytes back, so the
    ///     assertions read what would reach the broker rather than the message object the outbox held.
    ///     <see cref="ArrayBufferWriter{T}" /> stands in for the pooled writer the NATS client supplies.
    /// </summary>
    private static T RoundTrip<T>(MessageParser<T> parser, IMessage message)
        where T: IMessage<T>
    {
        var writer = new ArrayBufferWriter<byte>();

        NatsPublisher.SERIALIZER.Serialize(writer, message);

        return parser.ParseFrom(writer.WrittenSpan);
    }

    /// <summary>
    ///     Puts one instance the test can identify onto the free list, by taking back the one message
    ///     <paramref name="publisher" /> is expected to have pending and returning it the way the drain
    ///     loop would. Every rent afterwards hands this instance out before creating another, so a return
    ///     the publisher skips shows up as a stranger arriving in its place.
    /// </summary>
    private static IMessage SeedFreeList(NatsPublisher publisher) =>
        DequeueNext(publisher).Message;

    /// <summary>
    ///     The payload the NATS client hands its connection callbacks. Only the edge matters here, never
    ///     the message.
    /// </summary>
    private static NatsEventArgs ConnectionEvent() =>
        new ("test");

    /// <summary>
    ///     Sums the deltas recorded on <c>pulse.nats.connected</c> for as long as it is alive. The
    ///     exported gauge is delta-accumulated, so only the running total shows whether the +1 and the -1
    ///     stayed paired.
    /// </summary>
    private sealed class ConnectedGaugeProbe : IDisposable
    {
        private readonly MeterListener listener;

        private int net;

        public ConnectedGaugeProbe()
        {
            // Read the instrument before listening so it is already published when the listener starts;
            // during its own static initialization the field is still null and would not match.
            UpDownCounter<int> instrument = PulseMetrics.Nats.CONNECTED;

            listener = new MeterListener
            {
                InstrumentPublished = (published, self) =>
                {
                    if (ReferenceEquals(published, instrument))
                        self.EnableMeasurementEvents(published);
                },
            };

            listener.SetMeasurementEventCallback<int>((_, measurement, _, _) => Interlocked.Add(ref net, measurement));
            listener.Start();
        }

        /// <summary>
        ///     Running total of the recorded deltas: 1 while the connection is up, 0 once it is paired off.
        /// </summary>
        public int Net =>
            Volatile.Read(ref net);

        public void Dispose()
        {
            listener.Dispose();
        }
    }
}
