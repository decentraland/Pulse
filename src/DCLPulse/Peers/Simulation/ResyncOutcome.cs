namespace Pulse.Peers.Simulation;

/// <summary>
///     How a <c>RESYNC_REQUEST</c> was served. The two outcomes are measured apart because they
///     answer different questions about the same event: a targeted delta means the client's
///     baseline was still in the <see cref="SnapshotBoard" /> ring and the gap was cheap to close,
///     while a <see cref="FULL_STATE" /> fallback means the ring had moved past it (or
///     <c>Peers:ResyncWithDelta</c> is off) and the observer paid a full snapshot on the reliable
///     channel. Splitting them lets the fallback rate read off the histogram's own sample count.
/// </summary>
public enum ResyncOutcome : byte
{
    TARGETED_DELTA = 0,
    FULL_STATE = 1,
}

/// <summary>
///     Static tables over <see cref="ResyncOutcome" />, both indexed by the enum value: the
///     Prometheus label per outcome and the width every outcome-indexed array is allocated at.
/// </summary>
public static class ResyncOutcomes
{
    /// <summary>Prometheus <c>outcome</c> label per outcome — index matches the enum value.</summary>
    public static readonly string[] LABELS = ["delta", "full"];

    /// <summary>
    ///     Length of an array indexed by <c>(int)ResyncOutcome</c>, derived from
    ///     <see cref="LABELS" /> rather than written out again — a second hand-maintained number
    ///     could disagree with the labels silently, leaving an outcome recording into a series
    ///     nobody notices is missing.
    /// </summary>
    public static readonly int COUNT = LABELS.Length;
}
