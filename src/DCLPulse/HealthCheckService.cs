using System.Net;
using Microsoft.Extensions.Options;

namespace Pulse;

public sealed class HealthCheckService(
    ILogger<HealthCheckService> logger,
    IOptions<HealthCheckOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var prefix = $"http://+:{(int)options.Value.Port}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        logger.LogInformation("Health check listening on {Prefix}", prefix);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);
                ctx.Response.StatusCode = ctx.Request.Url?.AbsolutePath == "/health" ? 200 : 404;
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
