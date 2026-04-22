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
    EmoteStopReason? StopReason = null
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
///     Contains positional and animation state for at a given moment of time
/// </summary>
public record struct PeerSnapshot(

    // Server-related
    uint Seq,
    uint ServerTick,

    // Positional
    int Parcel,
    Vector3 LocalPosition,
    Vector3 GlobalPosition,
    Vector3 Velocity,
    float RotationY,

    // Animation-related
    int JumpCount,
    float MovementBlend,
    float SlideBlend,
    float? HeadYaw,
    float? HeadPitch,
    PlayerAnimationFlags AnimationFlags,
    GlideState GlideState,

    // Flags
    bool IsTeleport = false,

    // Emote — non-null means emote start or stop activity on this snapshot
    EmoteState? Emote = null
);
