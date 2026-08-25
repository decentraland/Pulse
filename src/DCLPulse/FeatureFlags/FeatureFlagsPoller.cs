namespace Pulse.FeatureFlags;

/// <summary>
///     Refetches the feature-flag document on a fixed interval and pushes each successful result
///     into <see cref="PulseFlagsConfigurationProvider" />. Exits immediately when
///     <see cref="FeatureFlagsOptions.Enabled" /> is false or
///     <see cref="FeatureFlagsOptions.PollIntervalSeconds" /> is zero, leaving the blocking first
///     load's result in effect for the process lifetime.
/// </summary>
public sealed class FeatureFlagsPoller(
    ILogger<FeatureFlagsPoller> logger,
    FeatureFlagsOptions options,
    FeatureFlagsClient client,
    PulseFlagsConfigurationProvider provider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogWarning("Feature flags poller disabled (FeatureFlags:Enabled is false)");
            return;
        }

        int intervalSeconds = options.PollIntervalSeconds;

        if (intervalSeconds <= 0)
        {
            logger.LogWarning("Feature flags poller disabled (FeatureFlags:PollIntervalSeconds is zero)");
            return;
        }

        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation(
            "Feature flags poller started — polling {Url} every {IntervalSeconds}s", client.Url, intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                provider.Apply(await client.FetchAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Feature flags poll failed; retaining previous configuration until next attempt");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
