using Decentraland.Pulse;
using Pulse.InterestManagement;
using System.Numerics;

namespace Pulse.Peers.Simulation;

/// <summary>
///     One-stop helper for "build a peer snapshot, publish it to the ring, and refresh the
///     spatial index". Every message handler that mutates peer state goes through here so the
///     snapshot construction (Seq numbering, parcel→global decoding, head-IK lifting from
///     <see cref="PlayerState" />, emote ledger stamping) lives in exactly one place.
///     <para />
///     Worker-shard isolation: Publish/Set on the underlying boards are per-slot single-writer.
///     This helper inherits that — only the owning worker may invoke it for a given peer.
/// </summary>
public sealed class PeerSnapshotPublisher(
    SnapshotBoard snapshotBoard,
    SpatialGrid spatialGrid,
    ParcelEncoder parcelEncoder,
    ITimeProvider timeProvider)
{
    /// <summary>
    ///     Caller-side description of an emote-start event. Holds only the fields the caller
    ///     genuinely owns (the emote identity, its duration, and an optional explicit start
    ///     tick for the back-dated reconnect path). The publisher fills the ledger bookkeeping:
    ///     <see cref="EmoteState.StartSeq" /> is set to the new snapshot's <c>Seq</c>, and
    ///     <see cref="EmoteState.StartTick" /> defaults to the snapshot's <c>ServerTick</c> when
    ///     <see cref="StartTick" /> is null — matching <c>EmoteStart</c>'s "started right now"
    ///     semantics. <c>HandshakeHandler</c> backdates explicitly to scrub the animation
    ///     forward by the offset the client reports.
    /// </summary>
    public readonly record struct EmoteInput(
        string EmoteId,
        uint? DurationMs = null,
        uint? StartTick = null,
        int? Mask = null);

    /// <summary>
    ///     Build a snapshot from a client-supplied <see cref="PlayerState" /> and publish it.
    ///     Used by movement input, emote start, and the handshake initial-state seed.
    ///     <para />
    ///     <paramref name="realm" /> is non-null only on the handshake seed path; movement and
    ///     emote-start publishes leave it null and inherit the prior snapshot's realm via the
    ///     <see cref="SnapshotBoard" /> ledger carry-forward.
    /// </summary>
    public PeerSnapshot PublishFromPlayerState(PeerIndex from, PlayerState state, EmoteInput? emote = null, string? realm = null)
    {
        uint seq = snapshotBoard.LastSeq(from) + 1;
        uint now = timeProvider.MonotonicTime;
        // Decode position once for the global position the server needs (AoI); every other field is
        // stored as the raw quantized code straight off the wire, no decode.
        Vector3 globalPosition = parcelEncoder.DecodeToGlobalPosition(state.ParcelIndex, state.GetPosition());

        EmoteState? emoteState = emote is { } e
            ? new EmoteState(
                EmoteId: e.EmoteId,
                StartSeq: seq,
                StartTick: e.StartTick ?? now,
                DurationMs: e.DurationMs,
                Mask: e.Mask)
            : null;

        QuantizedPointAt? pointAt = state.GetPointAtRaw() is { } p ? new QuantizedPointAt(p.X, p.Y, p.Z) : null;

        var snapshot = new PeerSnapshot(
            Seq: seq,
            ServerTick: now,
            Parcel: state.ParcelIndex,
            PositionX: state.PositionX,
            PositionY: state.PositionY,
            PositionZ: state.PositionZ,
            VelocityX: state.VelocityX,
            VelocityY: state.VelocityY,
            VelocityZ: state.VelocityZ,
            GlobalPosition: globalPosition,
            RotationY: state.RotationY,
            JumpCount: state.JumpCount,
            MovementBlend: state.MovementBlend,
            SlideBlend: state.SlideBlend,
            HeadYaw: state.HasHeadYaw ? state.HeadYaw : null,
            HeadPitch: state.HasHeadPitch ? state.HeadPitch : null,
            PointAt: pointAt,
            AnimationFlags: (PlayerAnimationFlags)state.StateFlags,
            GlideState: state.GlideState,
            Emote: emoteState,
            Realm: realm);

        snapshotBoard.Publish(from, in snapshot);
        spatialGrid.Set(from, snapshot.GlobalPosition);
        return snapshot;
    }

    /// <summary>
    ///     Publish a teleport snapshot. Velocity zeroed, animation snapped to
    ///     <see cref="PlayerAnimationFlags.Grounded" /> + <see cref="GlideState.PropClosed" />,
    ///     <see cref="PeerSnapshot.IsTeleport" /> set so the simulation broadcasts a
    ///     <c>TeleportPerformed</c> event. Rotation and head-IK are inherited from the prior
    ///     snapshot if one exists — otherwise zero / null.
    /// </summary>
    public PeerSnapshot PublishTeleport(PeerIndex from, int parcelIndex, Vector3 localPosition, string realm)
    {
        uint seq = snapshotBoard.LastSeq(from) + 1;
        uint now = timeProvider.MonotonicTime;

        // Encode the teleport's local position into the same quantized codes the wire uses by
        // round-tripping through the generated setters — keeps min/max/bits in lockstep with the
        // proto instead of duplicating them here.
        var encoded = new PlayerState
        {
            PositionXQuantized = localPosition.X,
            PositionYQuantized = localPosition.Y,
            PositionZQuantized = localPosition.Z,
        };

        // Derive the AoI position from the decoded codes, not the raw request floats: the setters
        // clamp to the quantization range, and observers only ever see the codes — using the raw
        // position here would index the peer where no observer renders it. The cache reset is
        // required because the setters memoize their unclamped input.
        encoded.ResetDecodedCache();
        Vector3 globalPosition = parcelEncoder.DecodeToGlobalPosition(parcelIndex, encoded.GetPosition());

        uint rotationY = 0;
        uint? headYaw = null, headPitch = null;
        QuantizedPointAt? pointAt = null;

        if (snapshotBoard.TryRead(from, out PeerSnapshot prev))
        {
            rotationY = prev.RotationY;
            headYaw = prev.HeadYaw;
            headPitch = prev.HeadPitch;
            pointAt = prev.PointAt;
        }

        var snapshot = new PeerSnapshot(
            Seq: seq,
            ServerTick: now,
            Parcel: parcelIndex,
            PositionX: encoded.PositionX,
            PositionY: encoded.PositionY,
            PositionZ: encoded.PositionZ,
            VelocityX: 0,
            VelocityY: 0,
            VelocityZ: 0,
            GlobalPosition: globalPosition,
            RotationY: rotationY,
            JumpCount: 0,
            MovementBlend: 0,
            SlideBlend: 0,
            HeadYaw: headYaw,
            HeadPitch: headPitch,
            PointAt: pointAt,
            AnimationFlags: PlayerAnimationFlags.Grounded,
            GlideState: GlideState.PropClosed,
            IsTeleport: true,
            Realm: realm);

        snapshotBoard.Publish(from, in snapshot);
        spatialGrid.Set(from, snapshot.GlobalPosition);
        return snapshot;
    }
}
