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

    /// <summary>
    ///     Fixture scene-listener budget. It is cumulative over realms *and* parcels: each realm
    ///     costs <c>FieldValidator.REALM_BUDGET_COST</c> (4) on top of its rect areas, so 16 admits
    ///     one realm of up to 12 parcels, or three realms of one parcel each.
    /// </summary>
    private FieldValidator Create(int maxRealmLength = 128, uint maxDurationMs = 60_000, int maxParcels = 16) =>
        new (Options.Create(new FieldValidatorOptions
        {
            MaxRealmLength = maxRealmLength,
            MaxEmoteDurationMs = maxDurationMs,
        }), Options.Create(new SceneListenerOptions { MaxParcels = maxParcels }), parcelEncoder,
            SceneListenerTestFactory.CellMapper(), transport);

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

    private static SceneListenerHandshakeRequest ListenerRequest(string realm, params (int MinX, int MinZ, int MaxX, int MaxZ)[] rects) =>
        ListenerRequest(Aoi(realm, rects));

    private static SceneListenerHandshakeRequest ListenerRequest(params SceneListenerAoi[] aoi)
    {
        var request = new SceneListenerHandshakeRequest();
        request.Aoi.AddRange(aoi);

        return request;
    }

    private static SceneListenerAoi Aoi(string realm, params (int MinX, int MinZ, int MaxX, int MaxZ)[] rects)
    {
        var announced = new SceneListenerAoi { Realm = realm };

        foreach ((int minX, int minZ, int maxX, int maxZ) in rects)
            announced.ParcelRects.Add(new ParcelRect { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ });

        return announced;
    }

    /// <summary>The parcels validated for one realm, or null when validation rejected the AoI.</summary>
    private static HashSet<int>? Parcels(SceneListenerState? listener, string realm = "main") =>
        listener is not null && listener.ParcelsByRealm.TryGetValue(realm, out HashSet<int>? parcels) ? parcels : null;

    [Test]
    public void SceneListener_ValidSingleCellRect_ExpandsToOneParcel()
    {
        FieldValidator v = Create();
        bool ok = v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", (10, 10, 10, 10)), out SceneListenerState? listener);

        Assert.That(ok, Is.True);
        Assert.That(Parcels(listener), Is.EquivalentTo(new[] { parcelEncoder.Encode(10, 10) }));
    }

    [Test]
    public void SceneListener_ValidRects_ExpandToUnionOfParcels()
    {
        FieldValidator v = Create();
        // A 2×2 rect (4 parcels) plus a disjoint 1×1 rect (1 parcel); one realm + Σ area = 4 + 5 ≤ 16.
        bool ok = v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest("main", (10, 10, 11, 11), (20, 20, 20, 20)), out SceneListenerState? listener);

        Assert.That(ok, Is.True);
        Assert.That(Parcels(listener), Is.EquivalentTo(new[]
        {
            parcelEncoder.Encode(10, 10), parcelEncoder.Encode(11, 10),
            parcelEncoder.Encode(10, 11), parcelEncoder.Encode(11, 11),
            parcelEncoder.Encode(20, 20),
        }));
    }

    [Test]
    public void SceneListener_OriginCrossingRect_ExpandsSignAgnostically()
    {
        FieldValidator v = Create();
        // A rect spanning the parcel-coordinate origin: (-1,-1)..(0,0) → 4 + area 4 ≤ 16.
        // Pins that expansion is sign-agnostic against future encoder refactors.
        bool ok = v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest("main", (-1, -1, 0, 0)), out SceneListenerState? listener);

        Assert.That(ok, Is.True);
        Assert.That(Parcels(listener), Is.EquivalentTo(new[]
        {
            parcelEncoder.Encode(-1, -1), parcelEncoder.Encode(0, -1),
            parcelEncoder.Encode(-1, 0), parcelEncoder.Encode(0, 0),
        }));
    }

    [Test]
    public void SceneListener_InvertedRect_Rejects()
    {
        FieldValidator v = Create();
        // MinX > MaxX — a degenerate rect the loop-based expansion would silently skip.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", (11, 10, 10, 10)), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_OutOfBoundsCorner_Rejects()
    {
        FieldValidator v = Create();
        // Aliasing guard: a 1×1 rect (well within the budget) whose coordinate is far out of bounds.
        // Encode alone would map it to a valid-looking index in another row instead of failing;
        // IsValidCoordinate is what rejects it here, not the area budget.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", (9999, 10, 9999, 10)), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_NominalAreaOverCap_Rejects()
    {
        FieldValidator v = Create();
        // Single 4×4 rect = 16 nominal parcels; with the realm charge that is 20 > cap 16. The
        // budget is enforced before expansion.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main", (10, 10, 13, 13)), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_OverlappingRectsWithinCap_DedupUnion()
    {
        FieldValidator v = Create();
        // Two identical 2×2 rects: 4 + Σ nominal area 8 = 12 ≤ 16, so accepted; the union dedups to 4.
        bool ok = v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest("main", (10, 10, 11, 11), (10, 10, 11, 11)), out SceneListenerState? listener);

        Assert.That(ok, Is.True);
        Assert.That(Parcels(listener)!.Count, Is.EqualTo(4));
    }

    [Test]
    public void SceneListener_EmptyRealm_Rejects()
    {
        FieldValidator v = Create();
        PeerState state = NewState();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, state, ListenerRequest("", (10, 10, 10, 10)), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
    }

    [Test]
    public void SceneListener_EmptyRectList_Rejects()
    {
        FieldValidator v = Create();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest("main"), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_MultipleRealms_ExpandPerRealm()
    {
        FieldValidator v = Create();
        // Two worlds, each with a scene at the same parcel — the shape a cohosting server
        // announces, and the one a single flat parcel set could not express.
        bool ok = v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest(Aoi("world-a", (0, 0, 0, 0)), Aoi("world-b", (0, 0, 1, 0))),
            out SceneListenerState? listener);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(Parcels(listener, "world-a"), Is.EquivalentTo(new[] { parcelEncoder.Encode(0, 0) }));
            Assert.That(Parcels(listener, "world-b"),
                Is.EquivalentTo(new[] { parcelEncoder.Encode(0, 0), parcelEncoder.Encode(1, 0) }));
        });
    }

    [Test]
    public void SceneListener_AreaBudgetSpansRealms_Rejects()
    {
        FieldValidator v = Create();
        // Two realms of 2×2 spend 2 × (4 + 4) = 16, exactly the fixture cap; a third realm of a
        // single parcel exceeds it, so extra realms buy no extra area.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest(Aoi("a", (10, 10, 11, 11)), Aoi("b", (10, 10, 11, 11)), Aoi("c", (20, 20, 20, 20))),
            out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_ManyTinyRealmsWithinBudget_Accepted()
    {
        FieldValidator v = Create();
        // Three realms of one parcel each: 3 parcels of area, and 3 × (4 + 1) = 15 ≤ cap 16.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest(Aoi("a", (0, 0, 0, 0)), Aoi("b", (1, 0, 1, 0)), Aoi("c", (2, 0, 2, 0))),
            out _), Is.True);
    }

    [Test]
    public void SceneListener_RealmOverheadOverBudget_Rejects()
    {
        FieldValidator v = Create();
        // Four parcels of area in total — trivially under a parcels-only cap of 16 — but realms are
        // charged against the same budget, so 4 × (4 + 1) = 20 > 16. An announcement cannot spend a
        // parcel-shaped budget on realm-shaped memory.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest(Aoi("a", (0, 0, 0, 0)), Aoi("b", (1, 0, 1, 0)),
                Aoi("c", (2, 0, 2, 0)), Aoi("d", (3, 0, 3, 0))),
            out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_ValidAoi_CarriesCellCoverAndParcelCount()
    {
        FieldValidator v = Create();
        // The descriptor is assembled in one place, so a valid announcement comes back with the
        // cell cover the simulation queries the grid with, not just the parcel set.
        bool ok = v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest("main", (10, 10, 11, 11)), out SceneListenerState? listener);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(listener!.ParcelCount, Is.EqualTo(4));
            Assert.That(listener.CellKeys, Is.Not.Empty);
            Assert.That(listener.CellKeys, Is.Unique);
        });
    }

    [Test]
    public void SceneListener_RepeatedRealm_Rejects()
    {
        FieldValidator v = Create();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(),
            ListenerRequest(Aoi("main", (10, 10, 10, 10)), Aoi("main", (20, 20, 20, 20))), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_EmptyAoi_Rejects()
    {
        FieldValidator v = Create();

        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest(), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }

    [Test]
    public void SceneListener_RealmTooLong_Rejects()
    {
        FieldValidator v = Create();
        // Default fixture MaxRealmLength = 128; a 300-char realm exceeds it.
        Assert.That(v.ValidateSceneListenerHandshake(PEER, NewState(), ListenerRequest(new string('a', 300), (10, 10, 10, 10)), out _), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_HANDSHAKE_FIELD);
    }
}
