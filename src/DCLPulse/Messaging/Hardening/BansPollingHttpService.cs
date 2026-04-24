using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Polls comms-gatekeeper for the wallet ban list on a fixed interval and delegates the
///     resulting snapshot to <see cref="BanEnforcer" />. Pass-through when
///     <see cref="CommsBearerToken" /> is unset (local dev / CI) or when
///     <see cref="BansOptions.PollIntervalSeconds" /> is zero: the hosted service exits
///     immediately without starting, so <see cref="BanList" /> stays empty and handshake-time
///     enforcement becomes a constant-time no-op.
/// </summary>
public sealed class BansPollingHttpService(
    ILogger<BansPollingHttpService> logger,
    IOptions<BansOptions> options,
    CommsBearerToken bearerToken,
    EnvName envName,
    BanEnforcer banEnforcer) : BackgroundService
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new ()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(bearerToken.Value))
        {
            logger.LogWarning("Bans disabled (COMMS_MODERATOR_TOKEN not set)");
            return;
        }

        int intervalSeconds = options.Value.PollIntervalSeconds;

        if (intervalSeconds <= 0)
        {
            logger.LogWarning("Bans disabled (Bans:PollIntervalSeconds is zero)");
            return;
        }

        var url = $"https://comms-gatekeeper.decentraland.{envName.HttpSuffix}/bans";

        using var httpClient = new HttpClient
        {
            Timeout = options.Value.HttpTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(options.Value.HttpTimeoutSeconds)
                : Timeout.InfiniteTimeSpan,
        };

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", bearerToken.Value);

        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("Bans poller started — polling {Url} every {IntervalSeconds}s", url, intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnce(httpClient, url, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Bans poll failed; retaining previous ban list until next attempt");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnce(HttpClient httpClient, string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        BansResponse? parsed = await JsonSerializer.DeserializeAsync<BansResponse>(stream, JSON_OPTIONS, ct);

        if (parsed?.Data is null)
        {
            logger.LogWarning("Bans poll returned malformed payload; retaining previous list");
            return;
        }

        var addresses = new List<string>(parsed.Data.Count);

        foreach (BanEntry entry in parsed.Data)
            if (!string.IsNullOrEmpty(entry.BannedAddress))
                addresses.Add(entry.BannedAddress);

        banEnforcer.Apply(addresses);
    }

    private sealed class BansResponse
    {
        [JsonPropertyName("data")]
        public List<BanEntry>? Data { get; set; }
    }

    private sealed class BanEntry
    {
        [JsonPropertyName("bannedAddress")]
        public string? BannedAddress { get; set; }
    }
}
