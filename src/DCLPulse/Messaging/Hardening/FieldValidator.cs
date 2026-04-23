using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;
using Vector3Proto = Decentraland.Common.Vector3;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Validates client-supplied fields on post-auth messages before any handler work runs.
///     Each validation method returns <c>true</c> when the message is safe to process; on
///     failure the peer is disconnected with a message-specific
///     <see cref="DisconnectReason" /> and the method returns <c>false</c>.
///     <para />
///     Invoked on the owning worker thread; stateless beyond injected dependencies.
/// </summary>
public sealed class FieldValidator(
    IOptions<FieldValidatorOptions> options,
    ParcelEncoder parcelEncoder,
    ITransport transport)
    : PeerDefense(transport, PulseMetrics.Hardening.FIELD_VALIDATION_FAILED)
{
    private readonly int maxEmoteIdLength = options.Value.MaxEmoteIdLength;
    private readonly int maxRealmLength = options.Value.MaxRealmLength;
    private readonly uint maxEmoteDurationMs = options.Value.MaxEmoteDurationMs;

    public bool ValidatePlayerStateInput(PeerIndex from, PeerState state, PlayerStateInput input)
    {
        if (!IsValidParcel(input.State.ParcelIndex))
            return Reject(from, state, DisconnectReason.INVALID_INPUT_FIELD);

        if (!IsValidPlayerStateFloats(input.State))
            return Reject(from, state, DisconnectReason.INVALID_INPUT_FIELD);

        return true;
    }

    public bool ValidateEmoteStart(PeerIndex from, PeerState state, EmoteStart emote)
    {
        if (maxEmoteIdLength > 0 && emote.EmoteId.Length > maxEmoteIdLength)
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        if (maxEmoteDurationMs > 0 && emote.HasDurationMs && emote.DurationMs > maxEmoteDurationMs)
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        if (!IsValidParcel(emote.PlayerState.ParcelIndex))
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        if (!IsValidPlayerStateFloats(emote.PlayerState))
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        return true;
    }

    public bool ValidateTeleport(PeerIndex from, PeerState state, TeleportRequest request)
    {
        if (string.IsNullOrEmpty(request.Realm))
            return Reject(from, state, DisconnectReason.INVALID_TELEPORT_FIELD);

        if (maxRealmLength > 0 && request.Realm.Length > maxRealmLength)
            return Reject(from, state, DisconnectReason.INVALID_TELEPORT_FIELD);

        if (!IsValidParcel(request.ParcelIndex))
            return Reject(from, state, DisconnectReason.INVALID_TELEPORT_FIELD);

        if (!IsFinite(request.Position))
            return Reject(from, state, DisconnectReason.INVALID_TELEPORT_FIELD);

        return true;
    }

    private bool IsValidParcel(int index) => parcelEncoder.IsValidIndex(index);

    /// <summary>
    ///     Rejects NaN/±Infinity on every client-supplied float in <see cref="PlayerState" />.
    ///     These values would propagate into the snapshot ring and fan out to observers, where
    ///     NaN breaks interpolation and inflates the <c>IsSameState</c> diff check (NaN != NaN
    ///     under IEEE). Also rejects malformed proto with unset Position/Velocity, which would
    ///     otherwise NRE in the handler. Optional fields (head yaw/pitch) are checked only
    ///     when present.
    /// </summary>
    private static bool IsValidPlayerStateFloats(PlayerState s) =>
        s.Position is not null
     && s.Velocity is not null
     && IsFinite(s.Position)
     && IsFinite(s.Velocity)
     && float.IsFinite(s.RotationY)
     && float.IsFinite(s.MovementBlend)
     && float.IsFinite(s.SlideBlend)
     && (!s.HasHeadYaw || float.IsFinite(s.HeadYaw))
     && (!s.HasHeadPitch || float.IsFinite(s.HeadPitch));

    private static bool IsFinite(Vector3Proto? v) =>
        v is not null && float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
