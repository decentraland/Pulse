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

    private FieldValidator Create(int maxEmoteIdLength = 64, int maxRealmLength = 128, uint maxDurationMs = 60_000) =>
        new (Options.Create(new FieldValidatorOptions
        {
            MaxEmoteIdLength = maxEmoteIdLength,
            MaxRealmLength = maxRealmLength,
            MaxEmoteDurationMs = maxDurationMs,
        }), parcelEncoder, transport);

    private static PeerState NewState() => new (PeerConnectionState.AUTHENTICATED);

    private static PlayerState ValidPlayerState() =>
        new ()
        {
            ParcelIndex = 100,
            Position = new Vector3(),
            Velocity = new Vector3(),
        };

    private static EmoteStart ValidEmoteStart(string emoteId = "wave", uint? durationMs = 3000) =>
        new ()
        {
            EmoteId = emoteId,
            DurationMs = durationMs ?? 0,
            PlayerState = ValidPlayerState(),
        };

    private static TeleportRequest ValidTeleport(string realm = "main") =>
        new () { Realm = realm, ParcelIndex = 100, Position = new Vector3() };

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
    public void OversizedEmoteId_RejectsWithInvalidEmoteField()
    {
        FieldValidator v = Create(maxEmoteIdLength: 8);
        EmoteStart msg = ValidEmoteStart(emoteId: new string('x', 9));

        Assert.That(v.ValidateEmoteStart(PEER, NewState(),msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_EMOTE_FIELD);
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
    public void ZeroEmoteIdLength_DisablesLengthCheckOnly()
    {
        FieldValidator v = Create(maxEmoteIdLength: 0);
        EmoteStart msg = ValidEmoteStart(emoteId: new string('x', 5000));

        Assert.That(v.ValidateEmoteStart(PEER, NewState(),msg), Is.True,
            "Length check disabled → huge EmoteId accepted");
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

    // ── Numeric finiteness (NaN/Inf) ─────────────────────────────────

    private static PlayerStateInput InputWith(Action<PlayerState> mutate)
    {
        PlayerState s = ValidPlayerState();
        mutate(s);
        return new PlayerStateInput { State = s };
    }

    [Test]
    public void NaNPosition_InInput_Rejects()
    {
        FieldValidator v = Create();
        PlayerStateInput msg = InputWith(s => s.Position = new Vector3 { X = float.NaN, Y = 0, Z = 0 });

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_INPUT_FIELD);
    }

    [Test]
    public void InfinityVelocity_InInput_Rejects()
    {
        FieldValidator v = Create();
        PlayerStateInput msg = InputWith(s => s.Velocity = new Vector3 { X = 0, Y = float.PositiveInfinity, Z = 0 });

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_INPUT_FIELD);
    }

    [Test]
    public void NaNRotationY_InInput_Rejects()
    {
        FieldValidator v = Create();
        PlayerStateInput msg = InputWith(s => s.RotationY = float.NaN);

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
    }

    [Test]
    public void NaNMovementBlend_InInput_Rejects()
    {
        FieldValidator v = Create();
        PlayerStateInput msg = InputWith(s => s.MovementBlend = float.NaN);

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
    }

    [Test]
    public void NaNHeadYaw_InInput_Rejects()
    {
        FieldValidator v = Create();
        PlayerStateInput msg = InputWith(s => s.HeadYaw = float.NaN);

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
    }

    [Test]
    public void UnsetHeadYaw_IsIgnored()
    {
        // HasHeadYaw is false by default; finiteness check should skip it regardless of value.
        FieldValidator v = Create();
        var msg = new PlayerStateInput { State = ValidPlayerState() };

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.True);
    }

    [Test]
    public void NullPosition_InInput_Rejects()
    {
        // Malformed proto with unset Position would NRE in the handler; validator must reject.
        FieldValidator v = Create();
        var msg = new PlayerStateInput
        {
            State = new PlayerState { ParcelIndex = 100, Velocity = new Vector3() },
        };

        Assert.That(v.ValidatePlayerStateInput(PEER, NewState(), msg), Is.False);
    }

    [Test]
    public void NaNPosition_InEmote_RejectsWithEmoteField()
    {
        FieldValidator v = Create();
        EmoteStart msg = ValidEmoteStart();
        msg.PlayerState.Position = new Vector3 { X = float.NaN, Y = 0, Z = 0 };

        Assert.That(v.ValidateEmoteStart(PEER, NewState(), msg), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.INVALID_EMOTE_FIELD);
    }

    [Test]
    public void NaNPosition_InTeleport_RejectsWithTeleportField()
    {
        FieldValidator v = Create();
        TeleportRequest msg = ValidTeleport();
        msg.Position = new Vector3 { X = float.NaN, Y = 0, Z = 0 };

        Assert.That(v.ValidateTeleport(PEER, NewState(), msg), Is.False);
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
}
