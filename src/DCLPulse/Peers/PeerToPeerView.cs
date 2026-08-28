namespace Pulse.Peers;

/// <summary>
///     Represents the "knowledge" of the given peer about another peer.
///     Stored on the observer's worker — exclusive access, no locks.
/// </summary>
public struct PeerToPeerView
{
    /// <summary>
    ///     PeerIndex this view refers to
    /// </summary>
    public PeerIndex Onto;

    /// <summary>
    ///     The actual snapshot that was last sent to the observer about this subject.
    ///     Used as the baseline for computing diffs - avoids cross-worker reads of the subject's ring buffer.
    /// </summary>
    public PeerSnapshot LastSentSnapshot;

    /// <summary>
    ///     Tick counter of the last tick on which this view's subject was in the observer's
    ///     interest set. It means "still visible as of this tick", not "last updated on this tick":
    ///     an already-existing view is re-stamped before the tier gate and before the snapshot
    ///     read, so a coarsely tiered or quiet subject never looks stale. A view created on this
    ///     tick is stamped after both, once it has been seeded.
    /// </summary>
    public uint LastSeenTick;

    /// <summary>
    ///     The profile version last sent to the observer for this subject.
    ///     Compared against <see cref="Simulation.ProfileBoard" /> each tick to detect changes.
    /// </summary>
    public int LastSentProfileVersion;

    /// <summary>
    ///     The emote last sent to the observer for this subject, or null if idle.
    ///     Tracks EmoteId + StartTick for deduplication, DurationMs for server-side one-shot expiry.
    /// </summary>
    public EmoteState? LastSentEmote;

    /// <summary>
    ///     The sequence number of the last teleport snapshot sent to the observer for this subject.
    ///     Prevents duplicate teleport broadcasts and supports consecutive teleports.
    /// </summary>
    public uint? LastSentTeleportSeq;

    /// <summary>
    ///     Sequence number of the last seq-carrying message (STATE_FULL, STATE_DELTA, EMOTE_STARTED,
    ///     EMOTE_STOPPED, TELEPORT, PLAYER_JOINED) sent to the observer for this subject.
    ///     Safety net: any subsequent send where the new seq equals <see cref="LastSentSeq" /> is
    ///     a duplicate delivery bug in the simulation pipeline and gets logged as an error.
    /// </summary>
    public uint LastSentSeq;

    /// <summary>
    ///     Wallet the observer currently believes owns this PeerIndex. Captured at
    ///     <c>PlayerJoined</c> time. If <see cref="Simulation.IdentityBoard" /> now reports a
    ///     different wallet for the same PeerIndex, the slot has been aliased — emit
    ///     <c>PlayerLeft</c> and re-announce as new. Defense-in-depth: the transport-level
    ///     <see cref="PeerIndexAllocator" /> prevents aliasing by holding pending slots through
    ///     a grace period, but the simulation should not silently trust that invariant.
    /// </summary>
    public string? LastSentWalletId;
}
