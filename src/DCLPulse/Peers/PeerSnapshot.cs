using Decentraland.Pulse;
using System.Numerics;

namespace Pulse.Peers;

/// <summary>
///     Emote metadata carried on a snapshot. Null on PeerSnapshot means no emote activity.
///     <para />
///     <see cref="StartSeq" /> is the Seq of the real EmoteStart snapshot that originated the
///     current emote occurrence. Carry-forward snapshots inherit it verbatim, so
///     <c>snapshot.Seq == snapshot.Emote.StartSeq</c> uniquely identifies the real start event.
///     Using Seq (not ServerTick) as the discriminator is necessary because multiple snapshots
///     can share a ServerTick when e.g. a teleport and an emote-start are processed on the
///     same tick; Seq is monotonic and unique per snapshot.
/// </summary>
public record struct EmoteState(
    string? EmoteId,
    uint StartSeq,
    uint StartTick,
    uint? DurationMs = null,
    EmoteStopReason? StopReason = null,
    int? Mask = null
);

public static class PeerSnapshotExtensions
{
    /// <summary>
    ///     Whether the snapshot reflects an active emote. Under the SnapshotBoard emote ledger
    ///     the active state is carried forward onto every snapshot between start and stop, so
    ///     this reads meaningfully on any snapshot — not just the start event.
    /// </summary>
    public static bool IsEmoting(this in PeerSnapshot snapshot) =>
        snapshot.Emote?.EmoteId != null;
}

/// <summary>
///     A point-at target (absolute world hit position) stored as the raw quantized wire codes,
///     so it diffs and relays without a decode/re-encode round-trip. See <see cref="PeerSnapshot" />.
/// </summary>
public readonly record struct QuantizedPointAt(uint X, uint Y, uint Z);

/// <summary>
///     Contains positional and animation state for at a given moment of time.
///     <para />
///     Positional/animation fields hold the <b>raw quantized wire codes</b> — the same uint32 the
///     client sent and the server relays — not decoded floats. Diffing compares these codes
///     exactly: comparing decoded floats would let a sub-<c>TOLERANCE</c> one-code change slip
///     through (divergence between what the observer holds and what the server thinks it sent),
///     and would force a wasteful decode-on-receive / re-encode-on-send round-trip. The only
///     decoded value kept is <see cref="GlobalPosition" />, which the server itself consumes for
///     interest management; it is computed once at publish time.
///     <para />
///     <see cref="Realm" /> is the AoI partition the peer belongs to. Carried forward by
///     <see cref="Simulation.SnapshotBoard.Publish" /> onto every snapshot, same as
///     <see cref="Emote" /> — the handshake initial-state seed sets it, subsequent
///     <c>TeleportRequest</c>s may change it. A snapshot with <c>Realm == null</c> would make
///     the peer invisible to every observer and unable to observe anyone — that's why
///     <c>PlayerInitialState.realm</c> is mandatory.
/// </summary>
public record struct PeerSnapshot(

    // Server-related
    uint Seq,
    uint ServerTick,

    // Positional — raw quantized wire codes
    int Parcel,
    uint PositionX,
    uint PositionY,
    uint PositionZ,
    uint VelocityX,
    uint VelocityY,
    uint VelocityZ,

    // Decoded world position — the only decoded value the server itself consumes (AoI / SpatialGrid)
    Vector3 GlobalPosition,

    uint RotationY,

    // Animation-related — raw quantized wire codes (JumpCount is a plain count, not quantized)
    int JumpCount,
    uint MovementBlend,
    uint SlideBlend,
    uint? HeadYaw,
    uint? HeadPitch,
    QuantizedPointAt? PointAt,
    PlayerAnimationFlags AnimationFlags,
    GlideState GlideState,

    // Flags
    bool IsTeleport = false,

    // Emote — non-null means emote start or stop activity on this snapshot
    EmoteState? Emote = null,

    // Realm — non-null means this snapshot explicitly sets the realm (TeleportHandler). Null on
    // non-teleport publishes; the SnapshotBoard inherits the prior snapshot's realm so the latest
    // ring slot is always self-sufficient for AoI partitioning.
    string? Realm = null
);
