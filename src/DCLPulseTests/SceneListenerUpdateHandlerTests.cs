using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests;

/// <summary>
///     <see cref="SceneListenerUpdateHandler" />: who may reassign an AoI, what a valid update
///     replaces, and that a rejected one leaves the AoI in force.
/// </summary>
[TestFixture]
public class SceneListenerUpdateHandlerTests
{
    private const string REALM = "main";

    private ITransport transport;
    private ParcelEncoder parcelEncoder;
    private SceneListenerCellMapper cellMapper;
    private SceneListenerUpdateHandler handler;
    private Dictionary<PeerIndex, PeerState> peers;
    private PeerIndex peer;

    [SetUp]
    public void SetUp()
    {
        IOptions<ParcelEncoderOptions> parcelOptions = Options.Create(new ParcelEncoderOptions());
        parcelEncoder = new ParcelEncoder(parcelOptions);
        transport = Substitute.For<ITransport>();
        cellMapper = new SceneListenerCellMapper(new SpatialGrid(100, 100), parcelOptions);

        var timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(10_000u);

        handler = new SceneListenerUpdateHandler(
            Substitute.For<ILogger<SceneListenerUpdateHandler>>(),
            // Rate limiting is off here; ThrottlesRepeatedUpdates covers the bucket itself.
            new DiscreteEventRateLimiter(
                Options.Create(new DiscreteEventRateLimiterOptions { RatePerSecond = 0 }),
                timeProvider,
                Substitute.For<ITransport>()),
            new FieldValidator(
                Options.Create(new FieldValidatorOptions()),
                Options.Create(new SceneListenerOptions { MaxParcels = 16 }),
                parcelEncoder,
                cellMapper,
                transport));

        peer = new PeerIndex(1);
        peers = new Dictionary<PeerIndex, PeerState> { [peer] = ListenerAt(0, 0) };
    }

    [Test]
    public void ValidUpdate_ReplacesParcelSetAndCellCover()
    {
        handler.Handle(peers, peer, Update(Rect(5, 5, 6, 6)));

        SceneListenerState listener = peers[peer].SceneListener!;

        Assert.Multiple(() =>
        {
            Assert.That(listener.ParcelsByRealm[REALM], Is.EquivalentTo(Encode((5, 5), (6, 5), (5, 6), (6, 6))));
            Assert.That(listener.ParcelCount, Is.EqualTo(4));
            Assert.That(listener.CellKeys, Is.EquivalentTo(Cover(5, 5, 6, 6)),
                "The cover must be recomputed for the new rects, not carried over.");
            transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
        });
    }

    [Test]
    public void ValidUpdate_ReplacesRatherThanExtends()
    {
        handler.Handle(peers, peer, Update(Rect(5, 5, 5, 5)));

        Assert.That(peers[peer].SceneListener!.ParcelsByRealm[REALM], Is.EquivalentTo(Encode((5, 5))));
    }

    [Test]
    public void ValidUpdate_ReplacesTheRealmSet()
    {
        handler.Handle(peers, peer, Update(Aoi("world-b", Rect(5, 5, 5, 5))));

        Assert.That(peers[peer].SceneListener!.ParcelsByRealm.Keys, Is.EquivalentTo(new[] { "world-b" }),
            "The update replaces the AoI wholesale, realms included — the previous realm is dropped.");
    }

    [TestCaseSource(nameof(InvalidUpdates))]
    public void InvalidUpdate_DisconnectsAndLeavesTheAoiInForce(ClientMessage message)
    {
        SceneListenerState before = peers[peer].SceneListener!;

        handler.Handle(peers, peer, message);

        Assert.Multiple(() =>
        {
            Assert.That(peers[peer].SceneListener, Is.SameAs(before));
            transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_SCENE_LISTENER_FIELD);
        });
    }

    private static IEnumerable<TestCaseData> InvalidUpdates()
    {
        yield return new TestCaseData(Update(Array.Empty<SceneListenerAoi>())).SetName("no realms");
        yield return new TestCaseData(Update(Aoi(REALM))).SetName("a realm with no rects");
        yield return new TestCaseData(Update(Rect(6, 6, 5, 5))).SetName("inverted rect");
        yield return new TestCaseData(Update(Rect(0, 0, 100, 100))).SetName("over the parcel budget");
        yield return new TestCaseData(Update(Rect(int.MinValue, 0, int.MinValue, 0))).SetName("out of world bounds");
    }

    [Test]
    public void NonListenerPeer_IsDropped()
    {
        var player = new PeerState(PeerConnectionState.AUTHENTICATED);
        peers[peer] = player;

        handler.Handle(peers, peer, Update(Rect(5, 5, 5, 5)));

        Assert.Multiple(() =>
        {
            Assert.That(player.SceneListener, Is.Null);
            transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
        });
    }

    [Test]
    public void UnauthenticatedPeer_IsDropped()
    {
        peers[peer] = new PeerState(PeerConnectionState.PENDING_AUTH)
        {
            SceneListener = new SceneListenerState(
                new Dictionary<string, HashSet<int>> { [REALM] = new () { 1 } }, new long[] { 0L }),
        };

        handler.Handle(peers, peer, Update(Rect(5, 5, 5, 5)));

        Assert.That(peers[peer].SceneListener!.ParcelsByRealm[REALM], Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public void ThrottlesRepeatedUpdates()
    {
        var timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(10_000u);
        var throttleTransport = Substitute.For<ITransport>();

        var throttled = new SceneListenerUpdateHandler(
            Substitute.For<ILogger<SceneListenerUpdateHandler>>(),
            new DiscreteEventRateLimiter(
                Options.Create(new DiscreteEventRateLimiterOptions { RatePerSecond = 1, BurstCapacity = 1 }),
                timeProvider,
                throttleTransport),
            new FieldValidator(
                Options.Create(new FieldValidatorOptions()),
                Options.Create(new SceneListenerOptions { MaxParcels = 16 }),
                parcelEncoder,
                cellMapper,
                transport));

        throttled.Handle(peers, peer, Update(Rect(5, 5, 5, 5)));
        throttled.Handle(peers, peer, Update(Rect(7, 7, 7, 7)));

        Assert.Multiple(() =>
        {
            // The throttled update never reached the AoI; the accepted one is still in force.
            Assert.That(peers[peer].SceneListener!.ParcelsByRealm[REALM], Is.EquivalentTo(Encode((5, 5))));
            throttleTransport.Received(1).Disconnect(peer, DisconnectReason.DISCRETE_EVENT_RATE_EXCEEDED);
        });
    }

    private PeerState ListenerAt(int x, int z)
    {
        var parcels = new Dictionary<string, HashSet<int>>
        {
            [REALM] = new () { parcelEncoder.Encode(x, z) },
        };

        return new PeerState(PeerConnectionState.AUTHENTICATED)
        {
            SceneListener = new SceneListenerState(parcels, Cover(x, z, x, z)),
        };
    }

    private long[] Cover(int minX, int minZ, int maxX, int maxZ)
    {
        var keys = new HashSet<long>();
        cellMapper.AddCoveringCells(keys, minX, minZ, maxX, maxZ);

        return keys.ToArray();
    }

    private int[] Encode(params (int X, int Z)[] parcels) =>
        parcels.Select(p => parcelEncoder.Encode(p.X, p.Z)).ToArray();

    private static ParcelRect Rect(int minX, int minZ, int maxX, int maxZ) =>
        new () { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ };

    private static ClientMessage Update(params ParcelRect[] rects) =>
        Update(Aoi(REALM, rects));

    private static ClientMessage Update(params SceneListenerAoi[] aoi)
    {
        var update = new SceneListenerUpdate();
        update.Aoi.AddRange(aoi);

        return new ClientMessage { SceneListenerUpdate = update };
    }

    private static SceneListenerAoi Aoi(string realm, params ParcelRect[] rects)
    {
        var announced = new SceneListenerAoi { Realm = realm };
        announced.ParcelRects.AddRange(rects);

        return announced;
    }
}
