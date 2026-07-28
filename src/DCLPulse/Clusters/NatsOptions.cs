namespace Pulse.Clusters;

public sealed class NatsOptions
{
    public const string SECTION_NAME = "Nats";

    /// <summary>
    ///     Broker URL. Empty or unset disables the feed entirely: the publisher exits at startup
    ///     and <see cref="ClusterTracker" /> keeps running in stats-only mode. This is the rollback
    ///     path — Pulse's only broker dependency is publish-only and fail-soft.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    ///     Prepended to every published subject, so a deployment can namespace its feed away from
    ///     a broker shared with another environment. Empty by default, which yields the subjects
    ///     archipelago's consumers already expect.
    /// </summary>
    public string SubjectPrefix { get; set; } = string.Empty;

    /// <summary>
    ///     Reported as <c>server_name</c> on <c>engine.discovery</c>. Free-form: archipelago-stats
    ///     reads only the status payload's timestamp and user count, never the name.
    /// </summary>
    public string ServerName { get; set; } = "pulse";

    /// <summary>
    ///     Cadence of the <c>engine.discovery</c> heartbeat. Must stay well under the 90 s window
    ///     archipelago-stats uses to decide the service is healthy.
    /// </summary>
    public int DiscoveryIntervalMs { get; set; } = 10_000;

    /// <summary>
    ///     Capacity of the bounded hand-off channel between the tracker and the publisher.
    ///     On overflow the oldest queued message is dropped: every feed here is self-superseding,
    ///     so a stalled broker must never back-pressure the tracker.
    /// </summary>
    public int ChannelCapacity { get; set; } = 1024;

    /// <summary>
    ///     Whether the feed is configured at all. False means stats-only mode.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url);
}
