using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class TeleportHandlerTests
{
    private const uint MONOTONIC_TIME = 7_000;

    private ITimeProvider timeProvider;
    private SnapshotBoard snapshotBoard;
    private SpatialGrid spatialGrid;
    private ParcelEncoder parcelEncoder;
    private TeleportHandler handler;
    private Dictionary<PeerIndex, PeerState> peers;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(MONOTONIC_TIME);

        snapshotBoard = new SnapshotBoard(100, 10);
        spatialGrid = new SpatialGrid(100, 100);
        parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));

        handler = new TeleportHandler(
            Substitute.For<ILogger<TeleportHandler>>(),
            timeProvider,
            snapshotBoard,
            spatialGrid,
            parcelEncoder,
            new DiscreteEventRateLimiter(
                Options.Create(new DiscreteEventRateLimiterOptions { RatePerSecond = 0 }),
                timeProvider,
                Substitute.For<ITransport>()));

        peers = new Dictionary<PeerIndex, PeerState>();
    }

    [Test]
    public void Handle_AuthenticatedPeer_PublishesSnapshotWithRealm()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateTeleportMessage(realm: "realm-a"));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
        Assert.That(snapshot.IsTeleport, Is.True);
    }

    [Test]
    public void Handle_EmptyRealm_Rejected()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateTeleportMessage(realm: ""));

        Assert.That(snapshotBoard.LastSeq(peer), Is.EqualTo(uint.MaxValue),
            "No snapshot should be published when the realm is empty.");
    }

    [Test]
    public void Handle_UnauthenticatedPeer_Rejected()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.PENDING_AUTH);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateTeleportMessage(realm: "realm-a"));

        Assert.That(snapshotBoard.LastSeq(peer), Is.EqualTo(uint.MaxValue));
    }

    [Test]
    public void Handle_SameRealmReTeleport_PublishesNewSnapshotWithSameRealm()
    {
        // Same-realm re-teleport must be a valid, idempotent-for-visibility operation — the snapshot
        // is published so the client's position is refreshed, and the realm stays the same so AoI
        // partition membership is unchanged.
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateTeleportMessage(realm: "realm-a", position: new Vector3(1, 2, 3)));
        handler.Handle(peers, peer, CreateTeleportMessage(realm: "realm-a", position: new Vector3(4, 5, 6)));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-a"));
        Assert.That(snapshot.Seq, Is.EqualTo(1));
        Assert.That(snapshot.LocalPosition, Is.EqualTo(new Vector3(4, 5, 6)));
    }

    [Test]
    public void Handle_RealmChange_OverridesInheritedRealm()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        handler.Handle(peers, peer, CreateTeleportMessage(realm: "realm-a"));
        handler.Handle(peers, peer, CreateTeleportMessage(realm: "realm-b"));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Realm, Is.EqualTo("realm-b"));
    }

    [Test]
    public void Handle_UpdatesSpatialGrid()
    {
        var peer = new PeerIndex(1);
        peers[peer] = new PeerState(PeerConnectionState.AUTHENTICATED);
        snapshotBoard.SetActive(peer);

        int parcelIndex = parcelEncoder.Encode(5, 10);

        handler.Handle(peers, peer, CreateTeleportMessage(realm: "realm-a",
            parcelIndex: parcelIndex, position: new Vector3(2, 3, 4)));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        HashSet<PeerIndex>? gridPeers = spatialGrid.GetPeers(snapshot.GlobalPosition);
        Assert.That(gridPeers, Is.Not.Null);
        Assert.That(gridPeers, Does.Contain(peer));
    }

    private static ClientMessage CreateTeleportMessage(
        string realm,
        int parcelIndex = 0,
        Vector3? position = null)
    {
        Vector3 pos = position ?? Vector3.Zero;

        return new ClientMessage
        {
            Teleport = new TeleportRequest
            {
                Realm = realm,
                ParcelIndex = parcelIndex,
                Position = new Decentraland.Common.Vector3 { X = pos.X, Y = pos.Y, Z = pos.Z },
            },
        };
    }
}
