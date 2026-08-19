namespace Pulse.FeatureFlags;

/// <summary>
///     Adds the remote feature-flag document to the configuration pipeline. Appended last to
///     <c>builder.Configuration.Sources</c>, so it outranks <c>appsettings.json</c>,
///     <c>dynamicconfig.json</c> and environment variables.
/// </summary>
public sealed class PulseFlagsConfigurationSource(
    FeatureFlagsOptions options,
    DynamicConfigSchema schema,
    FeatureFlagsClient client,
    ILogger bootstrapLogger)
    : IConfigurationSource
{
    /// <summary>
    ///     The single provider this source builds. Registered in DI so
    ///     <see cref="FeatureFlagsPoller" /> pushes later documents into the same instance that
    ///     backs the configuration root.
    /// </summary>
    public PulseFlagsConfigurationProvider Provider { get; } = new (options, schema, client, bootstrapLogger);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}
