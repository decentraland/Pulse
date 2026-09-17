namespace Pulse.Presence;

/// <summary>
///     Knobs for the <c>engine.parcel_changes</c> presence feed (iteration-2 C1). <see cref="Enabled" />
///     defaults to true, but the NATS gating still applies: no <c>Nats:Url</c>, no broker, nothing published.
/// </summary>
public sealed class PresenceOptions
{
    public const string SECTION_NAME = "Presence";

    /// <summary>Rollback switch for this feed alone; clustering, <c>engine.islands</c> and stats stay on.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     How often a non-empty batch of changes goes out, and the window over which changes for one
    ///     wallet coalesce — a peer running across parcels costs one entry per interval, not per step.
    /// </summary>
    public int BatchIntervalMs { get; set; } = 2000;

    /// <summary>
    ///     How often the full state of this server goes out as a <c>snapshot=true</c> batch. A recovery
    ///     deadline rather than a refresh rate: it bounds how long a missed delta leaves stale state.
    /// </summary>
    public int SnapshotIntervalMs { get; set; } = 60000;
}
