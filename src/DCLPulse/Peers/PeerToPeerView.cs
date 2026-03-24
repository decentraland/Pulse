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
    ///     Last sent sequence number
    /// </summary>
    public uint LastSentSeq;

    /// <summary>
    ///     The actual snapshot that was last sent to the observer about this subject.
    ///     Used as the baseline for computing diffs - avoids cross-worker reads of the subject's ring buffer.
    /// </summary>
    public PeerSnapshot LastSentSnapshot;

    /// <summary>
    ///     Tick counter when this view was last touched by the simulation.
    ///     Used for epoch-based pruning — views with a stale tick are either
    ///     reset on re-entry (STATE_FULL) or swept periodically for memory cleanup.
    /// </summary>
    public uint LastSeenTick;

    /// <summary>
    ///     The emote ID last sent to the observer for this subject, or null if idle.
    ///     Compared against <see cref="Simulation.EmoteBoard" /> each tick to detect transitions.
    /// </summary>
    public string? LastSentEmoteId;

    /// <summary>
    ///     The profile version last sent to the observer for this subject.
    ///     Compared against <see cref="Simulation.ProfileBoard" /> each tick to detect changes.
    /// </summary>
    public int LastSentProfileVersion;
}
