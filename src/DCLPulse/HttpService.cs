using Microsoft.Extensions.Options;
using Pulse.Metrics;
using System.Net;

namespace Pulse;

public sealed class HttpService(
    ILogger<HttpService> logger,
    IOptions<HttpServiceOptions> options,
    IMetricsCollector metricsCollector) : BackgroundService
{
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

                switch (ctx.Request.Url?.AbsolutePath)
                {
                    case "/health":
                        ctx.Response.StatusCode = 200;
                        break;
                    case "/metrics":
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
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }
}
