using Microsoft.Extensions.Options;
using Pulse.FeatureFlags;
using Pulse.Metrics;
using System.Net;
using System.Text.Json;

namespace Pulse;

public sealed class HttpService(
    ILogger<HttpService> logger,
    IOptions<HttpServiceOptions> options,
    IMetricsCollector metricsCollector,
    MetricsBearerToken metricsBearerToken,
    PulseFlagsConfigurationProvider featureFlagsProvider) : BackgroundService
{
    private static readonly string COMMIT_HASH = Environment.GetEnvironmentVariable("COMMIT_HASH") ?? "unknown";

    // camelCase members, verbatim dictionary keys: the override map is keyed by configuration paths
    // ("Transport:Hardening:IpLimiter:Enabled") that must read back exactly as typed.
    private static readonly JsonSerializerOptions ABOUT_JSON = new () { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string host = OperatingSystem.IsWindows() ? "localhost" : "+";
        var prefix = $"http://{host}:{(int)options.Value.Port}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        logger.LogInformation("Health check listening on {Prefix}", prefix);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);

                try
                {
                    switch (ctx.Request.Url?.AbsolutePath)
                    {
                        case "/health":
                            ctx.Response.StatusCode = 200;
                            break;
                        case "/about":
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.OutputStream.WriteAsync(BuildAboutResponse(), stoppingToken);
                            break;
                        case "/metrics":
                            if (!AuthorizeMetrics(ctx.Request))
                            {
                                ctx.Response.StatusCode = 401;
                                break;
                            }

                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                            await using (var writer = new StreamWriter(ctx.Response.OutputStream))
                                PrometheusFormatter.Write(writer, metricsCollector.TakeSnapshot());
                            break;
                        default:
                            ctx.Response.StatusCode = 404;
                            break;
                    }

                    ctx.Response.Close();
                }
                catch (HttpListenerException ex)
                {
                    logger.LogWarning(ex, "Client disconnected mid-response");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    /// <summary>
    ///     Serialises the current <c>/about</c> body. Built per request rather than cached: the
    ///     feature-flag overrides change whenever a new remote document is applied, and the point of
    ///     reporting them is to show what this task is running right now.
    /// </summary>
    private byte[] BuildAboutResponse() =>
        JsonSerializer.SerializeToUtf8Bytes(
            new AboutResponse(COMMIT_HASH, featureFlagsProvider.AppliedOverrides), ABOUT_JSON);

    private bool AuthorizeMetrics(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(metricsBearerToken.Value))
            return true;

        string? header = request.Headers["Authorization"];

        return header is not null
               && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
               && header.AsSpan(7).Equals(metricsBearerToken.Value, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Body of <c>/about</c>. <paramref name="FeatureFlagOverrides" /> is the remote
    ///     configuration this task is running with, reported verbatim: the remote document may set
    ///     any configuration key, so whatever it sets is what appears here — on an endpoint that
    ///     takes no bearer token. Whoever authors the document decides what this endpoint publishes.
    /// </summary>
    private readonly record struct AboutResponse(
        string CommitHash,
        IReadOnlyDictionary<string, string?> FeatureFlagOverrides);
}
