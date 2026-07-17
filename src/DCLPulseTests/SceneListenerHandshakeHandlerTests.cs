using DCL.Auth;
using Decentraland.Pulse;
using Google.Protobuf;
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
using Pulse.Transport.Hardening;
using System.Text.Json;

namespace DCLPulseTests;

[TestFixture]
public class SceneListenerHandshakeHandlerTests
{
    private const string WALLET = "0xabc0000000000000000000000000000000000001";
    private const string EPHEMERAL = "0xdef0000000000000000000000000000000000002";
    private const string TIMESTAMP = "1700000000000";

    private SnapshotBoard snapshotBoard;
    private SpatialGrid spatialGrid;
    private ITransport transport;
    private IdentityBoard identityBoard;
    private ParcelEncoder parcelEncoder;
    private Dictionary<PeerIndex, PeerState> peers;
    private SceneListenerHandshakeHandler handler;
    private PeerIndex peer;

    [SetUp]
    public void SetUp()
    {
        snapshotBoard = new SnapshotBoard(100, 16);
        spatialGrid = new SpatialGrid(100, 100);
        IOptions<ParcelEncoderOptions> parcelOptions = Options.Create(new ParcelEncoderOptions());
        parcelEncoder = new ParcelEncoder(parcelOptions);
        transport = Substitute.For<ITransport>();
        identityBoard = new IdentityBoard(100);

        ISignatureVerifier verifier = Substitute.For<ISignatureVerifier>();
        verifier.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(10_000u);

        var fieldValidator = new FieldValidator(
            Options.Create(new FieldValidatorOptions { MaxRealmLength = 16, MaxEmoteDurationMs = 60_000 }),
            Options.Create(new SceneListenerOptions { MaxParcels = 8 }),
            parcelEncoder,
            transport);

        handler = new SceneListenerHandshakeHandler(
            messagePipe: new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10)),
            authChainValidator: new AuthChainValidator(verifier),
            peerStateFactory: new PeerStateFactory(),
            identityBoard: identityBoard,
            transport: transport,
            attemptPolicy: new HandshakeAttemptPolicy(
                Options.Create(new HandshakeAttemptPolicyOptions()),
                Substitute.For<ITransport>()),
            preAuthAdmission: new PreAuthAdmission(Options.Create(new PreAuthAdmissionOptions())),
            replayPolicy: new HandshakeReplayPolicy(
                Options.Create(new HandshakeReplayPolicyOptions { Enabled = false }),
                new PeerOptions(),
                Options.Create(new ENetTransportOptions { MaxPeers = 100 }),
                timeProvider,
                Substitute.For<ITransport>()),
            banList: new BanList(),
            fieldValidator: fieldValidator,
            cellMapper: new SceneListenerCellMapper(parcelEncoder, spatialGrid, parcelOptions),
            logger: Substitute.For<ILogger<SceneListenerHandshakeHandler>>());

        peer = new PeerIndex(1);
        peers = new Dictionary<PeerIndex, PeerState> { [peer] = new (PeerConnectionState.PENDING_AUTH) };
    }

    [Test]
    public void Handle_ValidRequest_AuthenticatesWithListenerDescriptor()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main", (10, 10, 11, 10)));

        PeerState state = peers[peer];
        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.AUTHENTICATED));
        Assert.That(state.SceneListener, Is.Not.Null);
        Assert.That(state.SceneListener!.Realm, Is.EqualTo("main"));
        Assert.That(state.SceneListener.Parcels, Is.EquivalentTo(new[] { parcelEncoder.Encode(10, 10), parcelEncoder.Encode(11, 10) }));
        Assert.That(state.SceneListener.CellKeys, Is.Not.Empty);
        Assert.That(state.WalletId, Is.EqualTo(WALLET).IgnoreCase);
    }

    [Test]
    public void Handle_ValidRequest_NeverRegistersAsSubject()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main", (10, 10, 10, 10)));

        Assert.That(snapshotBoard.TryRead(peer, out _), Is.False,
            "A listener must never own a SnapshotBoard slot — it would become visible to players.");
        Assert.That(spatialGrid.GetPeers(new System.Numerics.Vector3(0, 0, 0)), Is.Null.Or.Not.Contains(peer));
    }

    [Test]
    public void Handle_ValidRequest_RegistersIdentity()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main", (10, 10, 10, 10)));

        Assert.That(identityBoard.TryGetPeerIndexByWallet(WALLET, out PeerIndex found), Is.True);
        Assert.That(found, Is.EqualTo(peer));
    }

    [Test]
    public void Handle_DuplicateWallet_EvictsExistingSession()
    {
        var other = new PeerIndex(2);
        identityBoard.Set(other, WALLET);

        handler.Handle(peers, peer, BuildListenerHandshake("main", (10, 10, 10, 10)));

        transport.Received(1).Disconnect(other, DisconnectReason.DUPLICATE_SESSION);
    }

    [Test]
    public void Handle_OverCapParcels_RejectsBeforeAuthenticated()
    {
        // Fixture MaxParcels = 8; a single 3×3 rect nominally covers 9 parcels.
        handler.Handle(peers, peer, BuildListenerHandshake("main", (10, 10, 12, 12)));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void Handle_EmptyParcels_Rejects()
    {
        handler.Handle(peers, peer, BuildListenerHandshake("main"));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void Handle_AlreadyAuthenticatedPlayer_SilentlyDropped()
    {
        // An already-AUTHENTICATED player peer must not be convertible into a listener in place:
        // duplicate-session eviction can't fire (duplicatedPeer == from), so without the
        // PENDING_AUTH gate its live SnapshotBoard slot + SpatialGrid entry would linger as a
        // frozen ghost avatar. The gate silently drops the message and leaves the peer untouched.
        var player = new PeerState(PeerConnectionState.AUTHENTICATED) { WalletId = WALLET };
        peers[peer] = player;

        // Seed a player-phase snapshot slot so we can assert it survives untouched.
        snapshotBoard.SetActive(peer);
        snapshotBoard.Publish(peer, TestSnapshots.Make(seq: 7));

        handler.Handle(peers, peer, BuildListenerHandshake("main", (10, 10, 11, 11)));

        Assert.That(peers[peer], Is.SameAs(player), "The peer's state object must be the exact same instance — no conversion.");
        Assert.That(player.SceneListener, Is.Null, "No listener descriptor may be stamped onto an authenticated player.");
        Assert.That(player.ConnectionState, Is.EqualTo(PeerConnectionState.AUTHENTICATED), "Connection state must be unchanged.");
        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True, "The player's snapshot slot must remain active.");
        Assert.That(snapshot.Seq, Is.EqualTo(7u), "The player's snapshot slot must be untouched.");
        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void Handle_InvalidAuthChainJson_RespondsWithError()
    {
        var request = new SceneListenerHandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8("not json"),
            Realm = "main",
        };
        request.ParcelRects.Add(new ParcelRect { MinX = 10, MinZ = 10, MaxX = 10, MaxZ = 10 });

        handler.Handle(peers, peer, new ClientMessage { SceneListenerHandshake = request });

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_AUTH),
            "A parse failure responds with an error but leaves the peer awaiting a retry within the attempt budget.");
    }

    private ClientMessage BuildListenerHandshake(string realm, params (int MinX, int MinZ, int MaxX, int MaxZ)[] rects)
    {
        var ephemeralPayload =
            $"Decentraland Login\nEphemeral address: {EPHEMERAL}\nExpiration: 2099-01-01T00:00:00Z";

        string connectPayload = SignedFetch.BuildSignedFetchPayload("connect", "/", TIMESTAMP, "{}");

        var headers = new Dictionary<string, string>
        {
            ["x-identity-auth-chain-0"] = JsonSerializer.Serialize(
                new AuthLink(Type: "SIGNER", Payload: WALLET, Signature: string.Empty)),
            ["x-identity-auth-chain-1"] = JsonSerializer.Serialize(
                new AuthLink(Type: "ECDSA_EPHEMERAL", Payload: ephemeralPayload, Signature: "0xdeadbeef")),
            ["x-identity-auth-chain-2"] = JsonSerializer.Serialize(
                new AuthLink(Type: "ECDSA_SIGNED_ENTITY", Payload: connectPayload, Signature: "0xdeadbeef")),
            ["x-identity-timestamp"] = TIMESTAMP,
            ["x-identity-metadata"] = "{}",
        };

        var request = new SceneListenerHandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8(JsonSerializer.Serialize(headers)),
            Realm = realm,
        };

        foreach ((int minX, int minZ, int maxX, int maxZ) in rects)
            request.ParcelRects.Add(new ParcelRect { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ });

        return new ClientMessage { SceneListenerHandshake = request };
    }
}
