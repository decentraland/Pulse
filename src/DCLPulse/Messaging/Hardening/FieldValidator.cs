using System.Diagnostics.CodeAnalysis;
using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

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
    IOptions<SceneListenerOptions> sceneListenerOptions,
    ParcelEncoder parcelEncoder,
    ITransport transport)
    : PeerDefense(transport, PulseMetrics.Hardening.FIELD_VALIDATION_FAILED)
{
    private readonly int maxRealmLength = options.Value.MaxRealmLength;
    private readonly uint maxEmoteDurationMs = options.Value.MaxEmoteDurationMs;
    private readonly int maxSceneListenerParcels = sceneListenerOptions.Value.MaxParcels;

    public bool ValidatePlayerStateInput(PeerIndex from, PeerState state, PlayerStateInput input)
    {
        if (input.State == null)
            return Reject(from, state, DisconnectReason.INVALID_INPUT_FIELD);

        if (!IsValidParcel(input.State.ParcelIndex))
            return Reject(from, state, DisconnectReason.INVALID_INPUT_FIELD);

        if (!input.State.AreQuantizedFieldsInRange())
            return Reject(from, state, DisconnectReason.INVALID_INPUT_FIELD);

        return true;
    }

    public bool ValidateEmoteStart(PeerIndex from, PeerState state, EmoteStart emote)
    {
        if (maxEmoteDurationMs > 0 && emote.HasDurationMs && emote.DurationMs > maxEmoteDurationMs)
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        if (emote.PlayerState == null)
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        if (!IsValidParcel(emote.PlayerState.ParcelIndex))
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        if (!emote.PlayerState.AreQuantizedFieldsInRange())
            return Reject(from, state, DisconnectReason.INVALID_EMOTE_FIELD);

        return true;
    }

    /// <summary>
    ///     Validates the optional <see cref="PlayerInitialState" /> the client carries through
    ///     the handshake. The auth-chain itself was already accepted upstream — this gate keeps
    ///     a malformed asserted-state from poisoning the snapshot ring on the seed publish. The
    ///     reconnect/recovery path always carries InitialState (and realm); the legacy connect
    ///     path skips it and uses a follow-up <c>TeleportRequest</c> to set realm.
    ///     <para />
    ///     Mirrors <see cref="ValidatePlayerStateInput" /> + <see cref="ValidateEmoteStart" />:
    ///     same parcel and quantized-code-range checks for the embedded <see cref="PlayerState" />,
    ///     same length / duration caps for the optional emote fields, same non-empty + length rules
    ///     for the realm as <see cref="ValidateTeleport" /> — but only enforced when an
    ///     InitialState is actually present.
    /// </summary>
    public bool ValidateHandshakeInitialState(PeerIndex from, PeerState state, PlayerInitialState initial)
    {
        if (initial.State == null)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (!IsValidParcel(initial.State.ParcelIndex))
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (!initial.State.AreQuantizedFieldsInRange())
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (maxEmoteDurationMs > 0 && initial.HasEmoteDurationMs && initial.EmoteDurationMs > maxEmoteDurationMs)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (string.IsNullOrEmpty(initial.Realm))
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (maxRealmLength > 0 && initial.Realm.Length > maxRealmLength)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

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

        if (!request.AreQuantizedFieldsInRange())
            return Reject(from, state, DisconnectReason.INVALID_TELEPORT_FIELD);

        return true;
    }

    /// <summary>
    ///     Validates a scene-listener handshake: realm rules identical to
    ///     <see cref="ValidateTeleport" />, every announced rect well-formed and fully in
    ///     encodable bounds, and the Σ nominal rect area within
    ///     <see cref="SceneListenerOptions.MaxParcels" /> — rejected, never clamped. On success
    ///     <paramref name="parcels" /> holds the deduped union of expanded parcel indices.
    /// </summary>
    public bool ValidateSceneListenerHandshake(PeerIndex from, PeerState state, SceneListenerHandshakeRequest request,
        [NotNullWhen(true)] out HashSet<int>? parcels)
    {
        parcels = null;

        if (string.IsNullOrEmpty(request.Realm))
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (maxRealmLength > 0 && request.Realm.Length > maxRealmLength)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        if (request.ParcelRects.Count == 0)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        // Nominal-area budget: Σ (w×h) ≤ MaxParcels, enforced before any expansion so a
        // hostile payload can't buy CPU/memory with huge or heavily overlapping rects. The
        // deduped union is necessarily ≤ the sum, so no post-expansion cap is needed.
        // Trade-off: overlapping rects are budgeted by sum, not union — clients should
        // announce disjoint rects.
        long nominalArea = 0;

        foreach (ParcelRect rect in request.ParcelRects)
        {
            if (rect.MinX > rect.MaxX || rect.MinZ > rect.MaxZ)
                return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

            if (!parcelEncoder.IsValidCoordinate(rect.MinX, rect.MinZ)
                || !parcelEncoder.IsValidCoordinate(rect.MaxX, rect.MaxZ))
                return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

            nominalArea += (long)(rect.MaxX - rect.MinX + 1) * (rect.MaxZ - rect.MinZ + 1);

            if (nominalArea > maxSceneListenerParcels)
                return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);
        }

        var deduped = new HashSet<int>();

        foreach (ParcelRect rect in request.ParcelRects)
        {
            for (int z = rect.MinZ; z <= rect.MaxZ; z++)
                for (int x = rect.MinX; x <= rect.MaxX; x++)
                    deduped.Add(parcelEncoder.Encode(x, z));
        }

        parcels = deduped;
        return true;
    }

    private bool IsValidParcel(int index) => parcelEncoder.IsValidIndex(index);
}
