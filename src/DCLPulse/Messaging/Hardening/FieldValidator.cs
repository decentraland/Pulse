using System.Diagnostics.CodeAnalysis;
using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using Pulse.InterestManagement;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;
using Pulse.Transport.Hardening;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Validates client-supplied fields on post-auth messages before any handler work runs.
///     Each validation method returns <c>true</c> when the message is safe to process; on
///     failure the peer is disconnected with a message-specific
///     <see cref="DisconnectReason" /> and the method returns <c>false</c>.
///     <para />
///     Invoked on the owning worker thread; stateless beyond injected dependencies.
///     <para />
///     One deliberate mutation: every realm off the wire is rewritten in place to its canonical
///     lowercase form (<see cref="CanonicalName" />) as it is validated, which is what makes one
///     realm one partition however the client spelled it (iteration-2 C1.5).
/// </summary>
public sealed class FieldValidator(
    IOptions<FieldValidatorOptions> options,
    IOptions<SceneListenerOptions> sceneListenerOptions,
    ParcelEncoder parcelEncoder,
    SceneListenerCellMapper cellMapper,
    IpLimiter ipLimiter,
    ITransport transport)
    : PeerDefense(transport, PulseMetrics.Hardening.FIELD_VALIDATION_FAILED)
{
    /// <summary>
    ///     What one announced realm costs against <see cref="SceneListenerOptions.MaxParcels" />,
    ///     on top of its rect areas. A realm carries fixed overhead a parcel count cannot see — a
    ///     retained name of up to <see cref="FieldValidatorOptions.MaxRealmLength" /> chars, a
    ///     dictionary entry and a set header — which measures at roughly six parcel slots. Charging
    ///     four keeps one knob governing both dimensions, so an announcement cannot spend a
    ///     parcel-shaped budget on realm-shaped memory. Raise <c>MaxParcels</c> if a legitimate
    ///     fleet needs more realms.
    /// </summary>
    private const int REALM_BUDGET_COST = 4;

    private readonly int maxRealmLength = options.Value.MaxRealmLength;
    private readonly uint maxEmoteDurationMs = options.Value.MaxEmoteDurationMs;
    private readonly int maxSceneListenerBudget = sceneListenerOptions.Value.MaxParcels;

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

        initial.Realm = CanonicalName.Of(initial.Realm);

        // Before the length check: lowercasing can lengthen a string, and the cap bounds what is kept.
        if (maxRealmLength > 0 && initial.Realm.Length > maxRealmLength)
            return Reject(from, state, DisconnectReason.INVALID_HANDSHAKE_FIELD);

        return true;
    }

    public bool ValidateTeleport(PeerIndex from, PeerState state, TeleportRequest request)
    {
        if (string.IsNullOrEmpty(request.Realm))
            return Reject(from, state, DisconnectReason.INVALID_TELEPORT_FIELD);

        request.Realm = CanonicalName.Of(request.Realm);

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
    ///     encodable bounds, and the whole announcement within
    ///     <see cref="SceneListenerOptions.MaxParcels" /> — rejected, never clamped. On success
    ///     <paramref name="listener" /> is the descriptor to stamp onto the peer.
    /// </summary>
    public bool ValidateSceneListenerHandshake(PeerIndex from, PeerState state, SceneListenerHandshakeRequest request,
        [NotNullWhen(true)] out SceneListenerState? listener) =>
        ValidateSceneListenerAoi(from, state, request.Aoi, DisconnectReason.INVALID_HANDSHAKE_FIELD, out listener);

    /// <summary>
    ///     Validates a <see cref="SceneListenerUpdate" />: the same rules and budget as the
    ///     handshake's announcement, which this replaces wholesale.
    /// </summary>
    public bool ValidateSceneListenerUpdate(PeerIndex from, PeerState state, SceneListenerUpdate update,
        [NotNullWhen(true)] out SceneListenerState? listener) =>
        ValidateSceneListenerAoi(from, state, update.Aoi, DisconnectReason.INVALID_SCENE_LISTENER_FIELD, out listener);

    /// <summary>
    ///     Shared gate for both scene-listener announcements: checks every realm, bounds-checks
    ///     every rect, expands each realm's rects to parcel indices, accumulates the covering grid
    ///     cells, and returns the finished descriptor. The
    ///     <see cref="SceneListenerOptions.MaxParcels" /> budget spans the whole announcement —
    ///     realms and parcels alike, see <see cref="REALM_BUDGET_COST" /> — so neither extra realms
    ///     nor extra area can be bought by adding more of the other. <paramref name="reason" /> is
    ///     the message-specific disconnect reason, so a rejection still names the message that
    ///     carried the bad AoI.
    /// </summary>
    private bool ValidateSceneListenerAoi(PeerIndex from, PeerState state, IReadOnlyList<SceneListenerAoi> aoi,
        DisconnectReason reason, [NotNullWhen(true)] out SceneListenerState? listener)
    {
        listener = null;

        if (aoi.Count == 0)
            return Reject(from, state, reason);

        // A whitelisted source IP is the operator's statement that the host is trusted
        // infrastructure, and a cohosting fleet announces whatever its scenes cover — so the budget
        // is not applied to it at all. Resolved once, before the loop: the exemption must not be
        // able to change between two rects of the same announcement.
        bool enforceBudget = !ipLimiter.IsWhitelisted(from);

        long budget = 0;
        var expanded = new Dictionary<string, HashSet<int>>(aoi.Count);
        var cellKeys = new HashSet<long>();

        foreach (SceneListenerAoi realmAoi in aoi)
        {
            if (string.IsNullOrEmpty(realmAoi.Realm))
                return Reject(from, state, reason);

            // The announced realm is probed against RealmSpatialGrids, whose keys are the lowercase
            // names players are placed under, so "CozyFarm.dcl.eth" would otherwise observe nobody.
            realmAoi.Realm = CanonicalName.Of(realmAoi.Realm);

            if (maxRealmLength > 0 && realmAoi.Realm.Length > maxRealmLength)
                return Reject(from, state, reason);

            // One entry per realm: a repeat is a malformed announcement, not a merge — silently
            // unioning them would hide the client bug and make the budget ambiguous.
            if (expanded.ContainsKey(realmAoi.Realm))
                return Reject(from, state, reason);

            if (realmAoi.ParcelRects.Count == 0)
                return Reject(from, state, reason);

            budget += REALM_BUDGET_COST;

            if (enforceBudget && budget > maxSceneListenerBudget)
                return Reject(from, state, reason);

            // First pass: bounds-check and price the realm's rects before expanding any of them, so
            // a hostile payload cannot buy expansion work on its way to being rejected. The deduped
            // union is necessarily <= the nominal area, so no post-expansion cap is needed.
            // Trade-off: overlapping rects are budgeted by sum, not union — clients should announce
            // disjoint rects.
            long realmArea = 0;

            foreach (ParcelRect rect in realmAoi.ParcelRects)
            {
                if (rect.MinX > rect.MaxX || rect.MinZ > rect.MaxZ)
                    return Reject(from, state, reason);

                if (!parcelEncoder.IsValidCoordinate(rect.MinX, rect.MinZ)
                    || !parcelEncoder.IsValidCoordinate(rect.MaxX, rect.MaxZ))
                    return Reject(from, state, reason);

                long area = (long)(rect.MaxX - rect.MinX + 1) * (rect.MaxZ - rect.MinZ + 1);
                realmArea += area;
                budget += area;

                if (enforceBudget && budget > maxSceneListenerBudget)
                    return Reject(from, state, reason);
            }

            // Second pass, now that the realm is priced: size the set from the area the first pass
            // measured instead of growing it through a dozen reallocations, and take the covering
            // cells off each rect — this is the only point that holds both a rect and its set.
            // Clamped to the world's parcel count, which the deduped union cannot exceed — every
            // corner was bounds-checked above, so every index is in range. Load-bearing once the
            // budget is waived: realmArea is then the *nominal* sum, which overlapping rects inflate
            // without bound, and presizing to it asks for a set orders of magnitude larger than the
            // union it will hold.
            int worldParcels = parcelEncoder.MaxIndexExclusive;
            var deduped = new HashSet<int>((int)Math.Min(realmArea, worldParcels));

            foreach (ParcelRect rect in realmAoi.ParcelRects)
            {
                cellMapper.AddCoveringCells(cellKeys, rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ);

                // Every index this can add is already present once the realm holds the whole
                // world, so the expansion is skipped from there on — bounding it at one world
                // rather than at the nominal sum, which overlapping rects inflate without limit.
                // The cover above stays unconditional: it is O(covered cells) rather than O(area),
                // and running it for every rect keeps the cell set independent of rect order.
                if (deduped.Count >= worldParcels)
                    continue;

                for (int z = rect.MinZ; z <= rect.MaxZ; z++)
                    for (int x = rect.MinX; x <= rect.MaxX; x++)
                        deduped.Add(parcelEncoder.Encode(x, z));
            }

            expanded[realmAoi.Realm] = deduped;
        }

        var keys = new long[cellKeys.Count];
        cellKeys.CopyTo(keys);

        listener = new SceneListenerState(expanded, keys);
        return true;
    }

    private bool IsValidParcel(int index) => parcelEncoder.IsValidIndex(index);
}
