using Decentraland.Common;
using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.InterestManagement;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class FieldValidatorTests
{
    private static readonly PeerIndex PEER = new (1);

    private ITransport transport;
    private ParcelEncoder parcelEncoder;

    [SetUp]
    public void SetUp()
    {
        transport = Substitute.For<ITransport>();
        parcelEncoder = new ParcelEncoder(Options.Create(new ParcelEncoderOptions()));
    }

    private FieldValidator Create(int maxRealmLength = 128, uint maxDurationMs = 60_000, int maxParcels = 4) =>
        new (Options.Create(new FieldValidatorOptions
        {
            MaxRealmLength = maxRealmLength,
            MaxEmoteDurationMs = maxDurationMs,
        }), Options.Create(new SceneListenerOptions { MaxParcels = maxParcels }), parcelEncoder, transport);

    private static PeerState NewState() => new (PeerConnectionState.AUTHENTICATED);

    private static PlayerState ValidPlayerState() =>
        new () { ParcelIndex = 100 };

    private static EmoteStart ValidEmoteStart(string emoteId = "wave", uint? durationMs = 3000) =>
        new ()
        {
            EmoteId = emoteId,
            DurationMs = durationMs ?? 0,
            PlayerState = ValidPlayerState(),
        };

    private static TeleportRequest ValidTeleport(string realm = "main") =>
        new () { Realm = realm, ParcelIndex = 100 };

    // ── PlayerStateInput ─────────────────────────────────────────────

    [Test]
    public void ValidPlayerStateInput_Accepted()
    {
        FieldValidator v = Create();
        var msg = new PlayerStateInput { State = ValidPlayerState() };

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.True);
        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void OutOfRangeParcel_InInput_RejectsWithInvalidInputField()
    {
        FieldValidator v = Create();
        var msg = new PlayerStateInput { State = new PlayerState { ParcelIndex = -1 } };

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_INPUT_FIELD);
    }

    [Test]
    public void Rejection_FlipsStateToPendingDisconnect()
    {
        FieldValidator v = Create();
        PeerState state = NewState();
        var msg = new PlayerStateInput { State = new PlayerState { ParcelIndex = -1 } };

        v.ValidatePlayerStateInput(PEER, state, msg);

        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT),
            "A rejected peer must immediately transition to PENDING_DISCONNECT so subsequent "
          + "messages fail SkipFromUnauthorizedPeer before ENet confirms the disconnect");
    }

    [Test]
    public void ParcelIndexBeyondMax_InInput_Rejects()
    {
        FieldValidator v = Create();
        var msg = new PlayerStateInput { State = new PlayerState { ParcelIndex = parcelEncoder.MaxIndexExclusive } };

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_INPUT_FIELD);
    }

    // ── EmoteStart ───────────────────────────────────────────────────

    [Test]
    public void ValidEmoteStart_Accepted()
    {
        FieldValidator v = Create();
        Assert.That(v.ValidateEmoteStart(PEER, NewState(),ValidEmoteStart()), Is.True);
        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void ExcessiveDurationMs_Rejects()
    {
        FieldValidator v = Create(maxDurationMs: 10_000);
        EmoteStart msg = ValidEmoteStart(durationMs: 10_001);

        Assert.That(v.ValidateEmoteStart(PEER, NewState(),msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_EMOTE_FIELD);
    }

    [Test]
    public void OutOfRangeParcel_InEmote_Rejects()
    {
        FieldValidator v = Create();
        EmoteStart msg = ValidEmoteStart();
        msg.PlayerState = new PlayerState { ParcelIndex = -5 };

        Assert.That(v.ValidateEmoteStart(PEER, NewState(),msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_EMOTE_FIELD);
    }

    [Test]
    public void ZeroDurationMs_DisablesDurationCheckOnly()
    {
        FieldValidator v = Create(maxDurationMs: 0);
        EmoteStart msg = ValidEmoteStart(durationMs: uint.MaxValue);

        Assert.That(v.ValidateEmoteStart(PEER, NewState(),msg), Is.True);
    }

    // ── Teleport ─────────────────────────────────────────────────────

    [Test]
    public void ValidTeleport_Accepted()
    {
        FieldValidator v = Create();
        Assert.That(v.ValidateTeleport(PEER, NewState(),ValidTeleport()), Is.True);
        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void EmptyRealm_RejectsWithInvalidTeleportField()
    {
        FieldValidator v = Create();
        TeleportRequest msg = ValidTeleport(realm: "");

        Assert.That(v.ValidateTeleport(PEER, NewState(),msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_TELEPORT_FIELD);
    }

    [Test]
    public void OversizedRealm_Rejects()
    {
        FieldValidator v = Create(maxRealmLength: 16);
        TeleportRequest msg = ValidTeleport(realm: new string('r', 17));

        Assert.That(v.ValidateTeleport(PEER, NewState(),msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_TELEPORT_FIELD);
    }

    [Test]
    public void OutOfRangeParcel_InTeleport_Rejects()
    {
        FieldValidator v = Create();
        TeleportRequest msg = ValidTeleport();
        msg.ParcelIndex = parcelEncoder.MaxIndexExclusive + 1;

        Assert.That(v.ValidateTeleport(PEER, NewState(),msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_TELEPORT_FIELD);
    }

    // ── Quantized wire-code ranges ───────────────────────────────────
    // Position/velocity/rotation/head are quantized uint32 fields now, so they can't carry NaN/Inf.
    // But a hostile client can still send a raw code above the field's bit width — the server relays
    // codes verbatim, so such a code would decode far outside [min, max] and poison every observer's
    // view (and the server's own GlobalPosition). Reject before storing.

    [Test]
    public void OutOfRangeQuantizedCode_InInput_RejectsWithInvalidInputField()
    {
        FieldValidator v = Create();
        // position_x is an 8-bit field: 255 is the top legal code, 256 is not producible by the encoder.
        var msg = new PlayerStateInput { State = new PlayerState { ParcelIndex = 100, PositionX = 256 } };

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_INPUT_FIELD);
    }

    [Test]
    public void TopLegalQuantizedCodes_InInput_Accepted()
    {
        FieldValidator v = Create();
        // Boundary: the top code of every field must pass so honest clients at the range extremes
        // aren't disconnected.
        var msg = new PlayerStateInput
        {
            State = new PlayerState
            {
                ParcelIndex = 100,
                PositionX = 255, PositionY = 8191, PositionZ = 255,
                VelocityX = 255, VelocityY = 255, VelocityZ = 255,
                RotationY = 127, MovementBlend = 31, SlideBlend = 15,
                HeadYaw = 127, HeadPitch = 127,
                PointAtX = 131071, PointAtY = 127, PointAtZ = 131071,
            },
        };

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.True);
        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void OutOfRangeQuantizedCode_InEmote_RejectsWithInvalidEmoteField()
    {
        FieldValidator v = Create();
        EmoteStart msg = ValidEmoteStart();
        msg.PlayerState = new PlayerState { ParcelIndex = 100, VelocityX = 256 };

        Assert.That(v.ValidateEmoteStart(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_EMOTE_FIELD);
    }

    [Test]
    public void OutOfRangeQuantizedCode_InHandshakeInitialState_RejectsWithInvalidHandshakeField()
    {
        FieldValidator v = Create();
        var initial = new PlayerInitialState
        {
            State = new PlayerState { ParcelIndex = 100, PositionX = 256 },
            Realm = "main",
        };

        Assert.That(v.ValidateHandshakeInitialState(PEER, NewState(), initial), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void OutOfRangeQuantizedCode_InTeleport_RejectsWithInvalidTeleportField()
    {
        FieldValidator v = Create();
        TeleportRequest msg = ValidTeleport();
        msg.PositionY = 8192; // 13-bit field: 8191 is the top legal code.

        Assert.That(v.ValidateTeleport(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_TELEPORT_FIELD);
    }

    // ── SceneListener handshake ──────────────────────────────────────

    private static SceneListenerHandshakeRequest ListenerRequest(string realm, params int[] parcels)
    {
        var request = new SceneListenerHandshakeRequest { Realm = realm };
        request.ParcelIndices.AddRange(parcels);
        return request;
    }

    [Test]
    public void SceneListener_ValidRequest_ReturnsDedupedParcels()
    {
        FieldValidator v = Create();
        bool ok = v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", 10, 11, 10), out HashSet<int>? parcels);

        Assert.That(ok, Is.True);
        Assert.That(parcels, Is.EquivalentTo(new[] { 10, 11 }));
    }

    [Test]
    public void SceneListener_EmptyRealm_Rejects()
    {
        FieldValidator v = Create();
        PeerState state = NewState();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, state, ListenerRequest("", 10), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
    }

    [Test]
    public void SceneListener_EmptyParcelList_Rejects()
    {
        FieldValidator v = Create();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main"), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_InvalidParcelIndex_Rejects()
    {
        FieldValidator v = Create();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", -1), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_OverCapAfterDedup_Rejects()
    {
        FieldValidator v = Create();
        // Fixture MaxParcels = 4; five distinct parcels must reject, duplicates must not count.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", 1, 2, 3, 4, 5), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_DuplicatesWithinCap_Accepted()
    {
        FieldValidator v = Create();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", 1, 1, 2, 2, 3, 3), out HashSet<int>? parcels), Is.True);
        Assert.That(parcels!.Count, Is.EqualTo(3));
    }

    [Test]
    public void SceneListener_RealmTooLong_Rejects()
    {
        FieldValidator v = Create();
        // Default fixture MaxRealmLength = 128; a 300-char realm exceeds it.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest(new string('a', 300), 1), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }
}
