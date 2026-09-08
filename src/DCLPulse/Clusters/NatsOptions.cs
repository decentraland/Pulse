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
    ///     Configuration key for <see cref="ServerName" />, in the <c>Nats__ServerName</c>
    ///     environment form.
    /// </summary>
    public const string SERVER_NAME_KEY = SECTION_NAME + ":" + nameof(ServerName);

    /// <summary>
    ///     The unconfigured <see cref="ServerName" />: <c>pulse-</c> plus the machine name, which is
    ///     the pod name under default Kubernetes networking and the container id under plain Docker.
    ///     Resolved once — <see cref="Environment.MachineName" /> cannot change while the process
    ///     runs, and <c>server_name</c> has to be stable for its lifetime (C1.5).
    ///     <para />
    ///     Unique per <b>host</b>, not per process: under <c>hostNetwork: true</c> or a
    ///     deployment-wide <c>spec.hostname</c> every pod on a node resolves the node's name, and two
    ///     Pulse processes on one machine share it by definition. Those deployments have to configure
    ///     <see cref="ServerName" /> explicitly — nothing here can detect the collision, since each
    ///     process only ever sees its own value.
    /// </summary>
    public static readonly string HOST_DEFAULT_SERVER_NAME = "pulse-" + Environment.MachineName;

    private string serverName = string.Empty;

    /// <summary>
    ///     Identifies this instance on <c>engine.discovery</c> and, since iteration 2, on
    ///     <c>engine.parcel_changes</c> — where consumers key <c>seq</c> by it and a
    ///     <c>snapshot=true</c> batch replaces <em>everything they hold for that name</em>.
    ///     <para />
    ///     <b>Two replicas must never share a value.</b> If they do, each one's snapshot deletes the
    ///     other's whole population from every consumer's presence map, and the interleaved delta
    ///     streams read as a permanent <c>seq</c> gap that freezes consumers in between — all of it
    ///     silent on the Pulse side, since both instances look healthy. Unset or blank therefore
    ///     defaults to <see cref="HOST_DEFAULT_SERVER_NAME" /> rather than to a shared literal; set it
    ///     explicitly only to something already unique per process.
    /// </summary>
    public string ServerName
    {
        get => string.IsNullOrWhiteSpace(serverName) ? HOST_DEFAULT_SERVER_NAME : serverName;
        set => serverName = value ?? string.Empty;
    }

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
