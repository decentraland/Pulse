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

    private static PlayerState ValidPlayerState() => new () { ParcelIndex = 100 };

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
