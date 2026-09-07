using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Stats;
using System.Net;

namespace Pulse;

/// <summary>
///     The HTTP surface. <c>/metrics</c> lives here because it is the one route with a bearer token
///     and its own content type; everything else — the read-only stats surface of iteration-2 C2, plus
///     <c>/health</c> and <c>/about</c> — is answered by <see cref="StatsRouter" />, which needs no
///     <see cref="HttpListener" /> to be tested against the contract goldens.
/// </summary>
public sealed class HttpService(
    ILogger<HttpService> logger,
    IOptions<HttpServiceOptions> options,
    IMetricsCollector metricsCollector,
    MetricsBearerToken metricsBearerToken,
    StatsRouter statsRouter) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string host = OperatingSystem.IsWindows() ? "localhost" : "+";
        var prefix = $"http://{host}:{(int)options.Value.Port}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        logger.LogInformation("HTTP surface listening on {Prefix}", prefix);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);

                try
                {
                    if (ctx.Request.Url?.AbsolutePath == "/metrics")
                        await WriteMetricsAsync(ctx, stoppingToken);
                    else
                        await WriteStatsAsync(ctx, stoppingToken);

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

    private async Task WriteMetricsAsync(HttpListenerContext ctx, CancellationToken token)
    {
        if (!AuthorizeMetrics(ctx.Request))
        {
            ctx.Response.StatusCode = 401;
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";

        await using var writer = new StreamWriter(ctx.Response.OutputStream);

        PrometheusFormatter.Write(writer, metricsCollector.TakeSnapshot());

        await writer.FlushAsync(token);
    }

    private async Task WriteStatsAsync(HttpListenerContext ctx, CancellationToken token)
    {
        StatsResponse response = statsRouter.Handle(
            ctx.Request.Url?.AbsolutePath ?? "/", StatsQuery.Parse(ctx.Request.Url?.Query));

        ctx.Response.StatusCode = response.Status;

        if (response.Location is { } location)
            ctx.Response.Headers["Location"] = location;

        if (response.Body is not { } body) return;

        ctx.Response.ContentType = "application/json";

        await ctx.Response.OutputStream.WriteAsync(body, token);
    }

    private bool AuthorizeMetrics(HttpListenerRequest request)
    {
        if (string.IsNullOrEmpty(metricsBearerToken.Value))
            return true;

        string? header = request.Headers["Authorization"];

        return header is not null
               && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
               && header.AsSpan(7).Equals(metricsBearerToken.Value, StringComparison.Ordinal);
    }
}
