namespace Pulse.Clusters;

public sealed class NatsOptions
{
    public const string SECTION_NAME = "Nats";

    /// <summary>Configuration key for <see cref="Url" />, as <c>Nats__Url</c>.</summary>
    public const string URL_KEY = SECTION_NAME + ":" + nameof(Url);

    /// <summary>
    ///     Flat environment variable also accepted for <see cref="Url" /> — the name archipelago's
    ///     services already read. <see cref="URL_KEY" /> wins when both are set.
    /// </summary>
    public const string URL_ENV_ALIAS = "NATS_URL";

    /// <summary>
    ///     Broker URL, from <c>Nats__Url</c> or <see cref="URL_ENV_ALIAS" />. Empty or unset disables
    ///     the feed — the publisher exits at startup, <see cref="ClusterTracker" /> stays stats-only.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Configuration key for <see cref="ServerName" />, as <c>Nats__ServerName</c>.</summary>
    public const string SERVER_NAME_KEY = SECTION_NAME + ":" + nameof(ServerName);

    /// <summary>
    ///     The unconfigured <see cref="ServerName" />: <c>pulse-</c> plus the machine name — the pod
    ///     name under default Kubernetes networking, the container id under plain Docker, resolved once
    ///     since <c>server_name</c> must be stable for the process lifetime (C1.5).
    ///     Unique per <b>host</b>, not per process: under <c>hostNetwork: true</c> or a deployment-wide
    ///     <c>spec.hostname</c> every pod on a node resolves the node's name, so those deployments must
    ///     set <see cref="ServerName" /> explicitly.
    /// </summary>
    public static readonly string HOST_DEFAULT_SERVER_NAME = "pulse-" + Environment.MachineName;

    private string serverName = string.Empty;

    /// <summary>
    ///     Identifies this instance on <c>engine.discovery</c> and <c>engine.parcel_changes</c>.
    ///     <b>Two replicas must never share a value</b>: <c>seq</c> is keyed by it and a snapshot
    ///     replaces everything held under it, so each would erase the other's population — silently,
    ///     both instances healthy. Unset or blank defaults to <see cref="HOST_DEFAULT_SERVER_NAME" />.
    /// </summary>
    public string ServerName
    {
        get => string.IsNullOrWhiteSpace(serverName) ? HOST_DEFAULT_SERVER_NAME : serverName;
        set => serverName = value ?? string.Empty;
    }

    /// <summary>
    ///     Heartbeat cadence for <c>engine.discovery</c>. Must stay well under the 90 s window
    ///     archipelago-stats uses to decide the service is healthy.
    /// </summary>
    public int DiscoveryIntervalMs { get; set; } = 10_000;

    /// <summary>
    ///     Maximum distinct peers with an undelivered assignment; past it the longest-admitted is
    ///     evicted, which is all <c>dcl_pulse_nats_dropped_total</c> counts and the only signal to raise
    ///     this towards <c>Transport.MaxPeers</c>. A publish that threw is a different counter, which a
    ///     larger outbox only lengthens the stale backlog for. Topology is held separately.
    /// </summary>
    public int ChannelCapacity { get; set; } = 1024;

    /// <summary>Whether the feed is configured at all. False means stats-only mode.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url);
}

public static class NatsConfigurationExtensions
{
    /// <summary>
    ///     Accepts <see cref="NatsOptions.URL_ENV_ALIAS" /> as a second spelling of
    ///     <see cref="NatsOptions.URL_KEY" />, filling the key only when it is not already set — a
    ///     source layered on top would let the flat alias override an explicit <c>Nats__Url</c>.
    /// </summary>
    public static void AddNatsUrlAlias(this IConfigurationManager configuration, string? aliasValue)
    {
        if (string.IsNullOrWhiteSpace(aliasValue)) return;
        if (!string.IsNullOrWhiteSpace(configuration[NatsOptions.URL_KEY])) return;

        configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(NatsOptions.URL_KEY, aliasValue)]);
    }
}
