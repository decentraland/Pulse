namespace Pulse.Clusters;

public sealed class NatsOptions
{
    public const string SECTION_NAME = "Nats";

    /// <summary>
    ///     Configuration key for <see cref="Url" />, in the <c>Nats__Url</c> environment form.
    /// </summary>
    public const string URL_KEY = SECTION_NAME + ":" + nameof(Url);

    /// <summary>
    ///     Flat environment variable also accepted for <see cref="Url" />. This is the name
    ///     archipelago's services read (<c>@well-known-components/nats-component</c> does
    ///     <c>config.requireString("NATS_URL")</c>), so a deployment that injects one
    ///     platform-wide broker URL reaches Pulse too without a Pulse-specific variable.
    ///     <para />
    ///     <see cref="URL_KEY" /> wins when both are set — the service-specific key is the more
    ///     precise intent.
    /// </summary>
    public const string URL_ENV_ALIAS = "NATS_URL";

    /// <summary>
    ///     Broker URL. Empty or unset disables the feed entirely: the publisher exits at startup
    ///     and <see cref="ClusterTracker" /> keeps running in stats-only mode. This is the rollback
    ///     path — Pulse's only broker dependency is publish-only and fail-soft.
    ///     <para />
    ///     Populated from either <c>Nats__Url</c> or <see cref="URL_ENV_ALIAS" />; see
    ///     <see cref="NatsConfigurationExtensions.AddNatsUrlAlias" />.
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
    ///     Maximum number of distinct peers with an undelivered assignment. The outbox holds one
    ///     entry per peer, so this bounds it; past the bound the longest-waiting peer is evicted and
    ///     counted as a genuine drop. The topology snapshot is held separately in a latest-wins slot
    ///     and never counts against this.
    ///     <para />
    ///     Worth raising towards <c>Transport.MaxPeers</c> if <c>dcl_pulse_nats_dropped_total</c> is
    ///     ever non-zero: a stalled broker must degrade freshness, never lose a peer's assignment.
    /// </summary>
    public int ChannelCapacity { get; set; } = 1024;

    /// <summary>
    ///     Whether the feed is configured at all. False means stats-only mode.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url);
}

public static class NatsConfigurationExtensions
{
    /// <summary>
    ///     Accepts <see cref="NatsOptions.URL_ENV_ALIAS" /> as a second spelling of
    ///     <see cref="NatsOptions.URL_KEY" />, so CI can inject one broker URL under either name.
    ///     <para />
    ///     Deliberately fills the key only when it is not already set, rather than layering a source
    ///     on top: an added source would take precedence over the environment and let the flat alias
    ///     silently override an explicit <c>Nats__Url</c>.
    ///     <para />
    ///     The alias value is passed in rather than read from the environment here, so the
    ///     precedence rule is testable without mutating process state.
    /// </summary>
    public static void AddNatsUrlAlias(this IConfigurationManager configuration, string? aliasValue)
    {
        if (string.IsNullOrWhiteSpace(aliasValue)) return;
        if (!string.IsNullOrWhiteSpace(configuration[NatsOptions.URL_KEY])) return;

        configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(NatsOptions.URL_KEY, aliasValue)]);
    }
}
