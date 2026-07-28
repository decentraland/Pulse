namespace Pulse.Clusters;

public sealed class NatsOptions
{
    public const string SECTION_NAME = "Nats";

    /// <summary>
    ///     Configuration key for <see cref="Url" />, in the <c>Nats__Url</c> environment form.
    /// </summary>
    public const string URL_KEY = SECTION_NAME + ":" + nameof(Url);

    /// <summary>
    ///     Flat environment variable also accepted for <see cref="Url" />. It is the name
    ///     archipelago's services already read, so a deployment that injects one platform-wide broker
    ///     URL reaches Pulse too. <see cref="URL_KEY" /> wins when both are set.
    /// </summary>
    public const string URL_ENV_ALIAS = "NATS_URL";

    /// <summary>
    ///     Broker URL. Empty or unset disables the feed entirely: the publisher exits at startup and
    ///     <see cref="ClusterTracker" /> keeps running in stats-only mode. Populated from either
    ///     <c>Nats__Url</c> or <see cref="URL_ENV_ALIAS" />; see
    ///     <see cref="NatsConfigurationExtensions.AddNatsUrlAlias" />.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    ///     Prepended to every published subject, so a deployment can namespace its feed away from a
    ///     broker shared with another environment. Empty by default, yielding archipelago's subjects
    ///     verbatim.
    /// </summary>
    public string SubjectPrefix { get; set; } = string.Empty;

    /// <summary>
    ///     Reported as <c>server_name</c> on <c>engine.discovery</c>. Free-form — nothing keys off it.
    /// </summary>
    public string ServerName { get; set; } = "pulse";

    /// <summary>
    ///     Cadence of the <c>engine.discovery</c> heartbeat. Must stay well under the 90 s window
    ///     archipelago-stats uses to decide the service is healthy.
    /// </summary>
    public int DiscoveryIntervalMs { get; set; } = 10_000;

    /// <summary>
    ///     Maximum number of distinct peers with an undelivered assignment. Past the bound the
    ///     longest-admitted peer is evicted, which is the only thing
    ///     <c>dcl_pulse_nats_dropped_total</c> counts — so that counter, and only that counter, is the
    ///     signal to raise this towards <c>Transport.MaxPeers</c>. A publish that threw is
    ///     <c>dcl_pulse_nats_publish_failed_total</c>, which this lever cannot help: a larger outbox
    ///     only lengthens the stale backlog a recovered connection has to drain. The topology snapshot
    ///     is held separately and never counts here.
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
    ///     <see cref="NatsOptions.URL_KEY" />, filling the key only when it is not already set. A
    ///     source layered on top instead would take precedence over the environment and let the flat
    ///     alias silently override an explicit <c>Nats__Url</c>. The alias value is a parameter rather
    ///     than read from the environment here, so precedence is decided without touching process
    ///     state.
    /// </summary>
    public static void AddNatsUrlAlias(this IConfigurationManager configuration, string? aliasValue)
    {
        if (string.IsNullOrWhiteSpace(aliasValue)) return;
        if (!string.IsNullOrWhiteSpace(configuration[NatsOptions.URL_KEY])) return;

        configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(NatsOptions.URL_KEY, aliasValue)]);
    }
}
