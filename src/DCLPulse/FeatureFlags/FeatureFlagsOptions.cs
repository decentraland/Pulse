namespace Pulse.FeatureFlags;

/// <summary>
///     Binding for the <c>FeatureFlags</c> configuration section. Read while the configuration is
///     still being built, before DI exists, so these can never themselves be set from the remote
///     document.
/// </summary>
public sealed class FeatureFlagsOptions
{
    public const string SECTION_NAME = "FeatureFlags";

    /// <summary>
    ///     Master switch. When false neither the blocking first load nor the poller runs, and the
    ///     server behaves as it would with no remote document.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Origin of the feature-flag service, without a trailing path. Null derives it from
    ///     <see cref="EnvName.HttpSuffix" /> as <c>https://feature-flags.decentraland.{suffix}</c>.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    ///     How often <see cref="FeatureFlagsPoller" /> refetches the document. Zero disables the
    ///     poller, leaving the blocking first load's result in effect for the process lifetime.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>HTTP request timeout for each fetch, in seconds. Zero means no timeout.</summary>
    public int HttpTimeoutSeconds { get; set; } = 10;

    /// <summary>
    ///     How long startup waits for the very first document before continuing on the shipped
    ///     defaults. Zero skips the blocking load entirely and leaves the first fetch to the poller.
    /// </summary>
    public int InitialFetchTimeoutSeconds { get; set; } = 5;

    /// <summary>
    ///     Unleash application name. Selects the document (<c>{AppName}.json</c>) and is the prefix
    ///     stripped from every flag and variant key.
    /// </summary>
    public string AppName { get; set; } = "pulse";

    /// <summary>
    ///     Value sent as the <c>referer</c> header, which Unleash's hostname strategy matches on.
    ///     The header is omitted when this is unset.
    /// </summary>
    public string? Hostname { get; set; }
}
