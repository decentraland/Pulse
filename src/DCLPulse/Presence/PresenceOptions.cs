namespace Pulse.Presence;

/// <summary>
///     Knobs for the <c>engine.parcel_changes</c> presence feed (iteration-2 C1).
///     <para />
///     <see cref="Enabled" /> defaults to true, but the feed still follows the existing NATS gating:
///     with <c>Nats:Url</c> unset there is no broker and nothing is published, exactly as before this
///     feed existed. So a deploy that changes no configuration behaves as it does today, and a
///     deployment that already points Pulse at a broker gets the feed.
/// </summary>
public sealed class PresenceOptions
{
    public const string SECTION_NAME = "Presence";

    /// <summary>
    ///     Feature flag. False stops the feed while leaving clustering, <c>engine.islands</c> and the
    ///     stats surface untouched — the rollback switch for this feed alone.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     How often a non-empty batch of changes goes out. Also the window over which changes for one
    ///     wallet coalesce, so a peer running across parcels costs one entry per interval rather than
    ///     one per step.
    /// </summary>
    public int BatchIntervalMs { get; set; } = 2000;

    /// <summary>
    ///     How often the full state of this server goes out as a <c>snapshot=true</c> batch. This is
    ///     the bound on how long a consumer that missed a delta — a broker outage, a publish that
    ///     threw — serves stale state before it is corrected, so it is a recovery deadline rather
    ///     than a refresh rate.
    /// </summary>
    public int SnapshotIntervalMs { get; set; } = 60000;
}
