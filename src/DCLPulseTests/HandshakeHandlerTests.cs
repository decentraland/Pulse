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
using System.Numerics;
using System.Text.Json;

namespace DCLPulseTests;

/// <summary>
///     End-to-end coverage of the handshake's <see cref="PlayerInitialState" /> seed path.
///     The auth-chain crypto is bypassed via a stub <see cref="ISignatureVerifier" /> that
///     accepts every signature, so we drive the real <see cref="HandshakeHandler.Handle" /> —
///     the integration we actually care about.
/// </summary>
[TestFixture]
public class HandshakeHandlerTests
{
    private const uint NOW_MS = 10_000;
    private const string WALLET = "0xabc0000000000000000000000000000000000001";
    private const string EPHEMERAL = "0xdef0000000000000000000000000000000000002";
    private const string TIMESTAMP = "1700000000000";

    private SnapshotBoard snapshotBoard;
    private SpatialGrid spatialGrid;
    private ParcelEncoder parcelEncoder;
    private ITimeProvider timeProvider;
    private ITransport transport;
    private IdentityBoard identityBoard;
    private Dictionary<PeerIndex, PeerState> peers;
    private HandshakeHandler handler;
    private PeerIndex peer;

    [SetUp]
    public void SetUp()
    {
        snapshotBoard = new SnapshotBoard(100, 16);
        spatialGrid = new SpatialGrid(100, 100);
        parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(NOW_MS);
        transport = Substitute.For<ITransport>();
        identityBoard = new IdentityBoard(100);

        ISignatureVerifier verifier = Substitute.For<ISignatureVerifier>();
        verifier.Verify(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var publisher = new PeerSnapshotPublisher(snapshotBoard, spatialGrid, parcelEncoder, timeProvider);

        var fieldValidator = new FieldValidator(
            Options.Create(new FieldValidatorOptions { MaxRealmLength = 16, MaxEmoteDurationMs = 60_000 }),
            Options.Create(new SceneListenerOptions()),
            parcelEncoder,
            transport);

        handler = new HandshakeHandler(
            messagePipe: new MessagePipe(Substitute.For<ILogger<MessagePipe>>(), new ServerMessageCounters(10)),
            authChainValidator: new AuthChainValidator(verifier),
            peerStateFactory: new PeerStateFactory(),
            snapshotBoard: snapshotBoard,
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
            snapshotPublisher: publisher,
            timeProvider: timeProvider,
            logger: Substitute.For<ILogger<HandshakeHandler>>());

        peer = new PeerIndex(1);
        peers = new Dictionary<PeerIndex, PeerState> { [peer] = new (PeerConnectionState.PENDING_AUTH) };
    }

    [Test]
    public void Handle_NoInitialState_AuthenticatesWithoutSeedingSnapshot()
    {
        // Legacy connect flow: client skips InitialState and relies on the follow-up
        // TeleportRequest to set realm. Only the reconnect/recovery flow carries InitialState
        // (where realm-validation kicks in).
        handler.Handle(peers, peer, BuildHandshake(initialState: null));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.AUTHENTICATED));

        Assert.That(snapshotBoard.LastSeq(peer), Is.EqualTo(uint.MaxValue),
            "Without InitialState the handshake leaves the snapshot ring untouched.");
    }

    [Test]
    public void Handle_FreshSlot_WithInitialState_SeedsSnapshotAtSeqZero()
    {
        PlayerInitialState initial = CreateInitialState(parcelIndex: 5,
            position: new Vector3(2f, 3f, 4f),
            velocity: new Vector3(0.5f, 0f, 0.5f),
            rotationY: 90f,
            stateFlags: (uint)PlayerAnimationFlags.Grounded,
            realm: "main");

        handler.Handle(peers, peer, BuildHandshake(initial));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.AUTHENTICATED));
        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Seq, Is.EqualTo(0u));
        Assert.That(snapshot.Parcel, Is.EqualTo(5));
        Vector3 position = snapshot.DecodePosition();
        Assert.That(position.X, Is.EqualTo(2f).Within(PlayerState.PositionXQuantizedStep));
        Assert.That(position.Y, Is.EqualTo(3f).Within(PlayerState.PositionYQuantizedStep));
        Assert.That(position.Z, Is.EqualTo(4f).Within(PlayerState.PositionZQuantizedStep));
        Vector3 velocity = snapshot.DecodeVelocity();
        Assert.That(velocity.X, Is.EqualTo(0.5f).Within(PlayerState.VelocityXQuantizedStep));
        Assert.That(velocity.Y, Is.EqualTo(0f).Within(PlayerState.VelocityYQuantizedStep));
        Assert.That(velocity.Z, Is.EqualTo(0.5f).Within(PlayerState.VelocityZQuantizedStep));
        Assert.That(snapshot.DecodeRotationY(), Is.EqualTo(90f).Within(PlayerState.RotationYQuantizedStep));
        Assert.That(snapshot.AnimationFlags, Is.EqualTo(PlayerAnimationFlags.Grounded));
        Assert.That(snapshot.IsTeleport, Is.False);

        Assert.That(snapshot.Realm, Is.EqualTo("main"),
            "Seed must carry the client-asserted realm so AoI can place the peer immediately on reconnect.");
    }

    [Test]
    public void Handle_EmptyRealmInInitialState_RejectsHandshake()
    {
        PlayerInitialState initial = CreateInitialState(parcelIndex: 0, realm: string.Empty);

        handler.Handle(peers, peer, BuildHandshake(initial));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT),
            "Empty realm must short-circuit the handshake — a seed without realm is invisible in AoI.");

        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        Assert.That(snapshotBoard.LastSeq(peer), Is.EqualTo(uint.MaxValue),
            "No snapshot must be published when validation aborts the handshake.");
    }

    [Test]
    public void Handle_RealmExceedsMaxLength_RejectsHandshake()
    {
        // MaxRealmLength = 16 in this fixture; anything longer must be rejected.
        PlayerInitialState initial = CreateInitialState(parcelIndex: 0, realm: new string('a', 17));

        handler.Handle(peers, peer, BuildHandshake(initial));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void Handle_WithEmoteInitialState_BackdatesStartTickByOffset()
    {
        PlayerInitialState initial = CreateInitialState(parcelIndex: 0,
            emoteId: "wave",
            emoteDurationMs: 3_000,
            emoteStartOffsetMs: 1_500);

        handler.Handle(peers, peer, BuildHandshake(initial));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote, Is.Not.Null);
        Assert.That(snapshot.Emote!.Value.EmoteId, Is.EqualTo("wave"));
        Assert.That(snapshot.Emote.Value.DurationMs, Is.EqualTo(3_000u));

        Assert.That(snapshot.Emote.Value.StartTick, Is.EqualTo(NOW_MS - 1_500u),
            "StartTick must be backdated by the offset so observers scrub the animation forward.");

        Assert.That(snapshot.Emote.Value.StartSeq, Is.EqualTo(snapshot.Seq),
            "Publisher must stamp StartSeq = Seq so ScanIntermediateEvents recognizes a real start.");
    }

    [Test]
    public void Handle_EmoteOffsetExceedsNow_ClampsStartTickToZero()
    {
        timeProvider.MonotonicTime.Returns(500u);

        PlayerInitialState initial = CreateInitialState(parcelIndex: 0,
            emoteId: "wave",
            emoteStartOffsetMs: 9_999);

        handler.Handle(peers, peer, BuildHandshake(initial));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);

        Assert.That(snapshot.Emote!.Value.StartTick, Is.EqualTo(0u),
            "Overstated offset must clamp to zero rather than wrap the unsigned tick.");
    }

    [Test]
    public void Handle_EmptyEmoteId_LeavesEmoteNull()
    {
        PlayerInitialState initial = CreateInitialState(parcelIndex: 0, emoteId: string.Empty);

        handler.Handle(peers, peer, BuildHandshake(initial));

        Assert.That(snapshotBoard.TryRead(peer, out PeerSnapshot snapshot), Is.True);
        Assert.That(snapshot.Emote, Is.Null);
    }

    [Test]
    public void Handle_InvalidParcelInInitialState_RejectsHandshake()
    {
        PlayerInitialState initial = CreateInitialState(parcelIndex: -1);

        handler.Handle(peers, peer, BuildHandshake(initial));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT),
            "FieldValidator must short-circuit the handshake before AUTHENTICATED.");

        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        Assert.That(snapshotBoard.LastSeq(peer), Is.EqualTo(uint.MaxValue),
            "No snapshot must be published when validation aborts the handshake.");
    }

    [Test]
    public void Handle_NullStateInsideInitialState_RejectsHandshake()
    {
        // Wire-level guard: client could ship PlayerInitialState with no PlayerState.
        handler.Handle(peers, peer, BuildHandshake(new PlayerInitialState()));

        Assert.That(peers[peer].ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
        transport.Received(1).Disconnect(peer, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void Handle_CleanupOfEvictedPeer_ThirdHandshakeStillEvictsLiveSession()
    {
        var peerO = new PeerIndex(1);
        var peerN = new PeerIndex(2);
        var peerT = new PeerIndex(3);

        peers = new Dictionary<PeerIndex, PeerState>
        {
            [peerO] = new (PeerConnectionState.PENDING_AUTH),
            [peerN] = new (PeerConnectionState.PENDING_AUTH),
            [peerT] = new (PeerConnectionState.PENDING_AUTH),
        };

        // Wallet W connects as O, then a second connection with W arrives as N and evicts O.
        handler.Handle(peers, peerO, BuildHandshake(initialState: null));
        handler.Handle(peers, peerN, BuildHandshake(initialState: null));
        transport.Received(1).Disconnect(peerO, DisconnectReason.DUPLICATE_SESSION);

        // O's DISCONNECTING cleanup runs ~5s later (CleanupDisconnectedPeer → identityBoard.Remove).
        identityBoard.Remove(peerO);

        Assert.That(identityBoard.TryGetPeerIndexByWallet(WALLET, out PeerIndex live), Is.True,
            "cleanup of the evicted peer must not delete the live wallet binding");
        Assert.That(live, Is.EqualTo(peerN));

        // A third handshake with W must still detect N as the duplicate session and evict it.
        handler.Handle(peers, peerT, BuildHandshake(initialState: null));
        transport.Received(1).Disconnect(peerN, DisconnectReason.DUPLICATE_SESSION);
    }

    private ClientMessage BuildHandshake(PlayerInitialState? initialState)
    {
        // The handshake handler reads x-identity-* headers from the JSON payload, parses the
        // ECDSA chain, then asks AuthChainValidator to recover signers. The substituted verifier
        // accepts everything, so we just need a structurally valid header bundle.
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

        var request = new HandshakeRequest
        {
            AuthChain = ByteString.CopyFromUtf8(JsonSerializer.Serialize(headers)),
        };

        if (initialState != null)
            request.InitialState = initialState;

        return new ClientMessage { Handshake = request };
    }

    private static PlayerInitialState CreateInitialState(
        int parcelIndex,
        Vector3? position = null,
        Vector3? velocity = null,
        float rotationY = 0f,
        uint stateFlags = 0,
        string? emoteId = null,
        uint? emoteDurationMs = null,
        uint? emoteStartOffsetMs = null,
        string realm = "main")
    {
        Vector3 pos = position ?? Vector3.Zero;
        Vector3 vel = velocity ?? Vector3.Zero;

        var initial = new PlayerInitialState
        {
            State = new PlayerState
            {
                ParcelIndex = parcelIndex,
                PositionXQuantized = pos.X,
                PositionYQuantized = pos.Y,
                PositionZQuantized = pos.Z,
                VelocityXQuantized = vel.X,
                VelocityYQuantized = vel.Y,
                VelocityZQuantized = vel.Z,
                RotationYQuantized = rotationY,
                StateFlags = stateFlags,
            },
            Realm = realm,
        };

        if (emoteId != null)
            initial.EmoteId = emoteId;

        if (emoteDurationMs.HasValue)
            initial.EmoteDurationMs = emoteDurationMs.Value;

        if (emoteStartOffsetMs.HasValue)
            initial.EmoteStartOffsetMs = emoteStartOffsetMs.Value;

        return initial;
    }
}
