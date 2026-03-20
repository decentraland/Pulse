using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace Pulse;

public class HealthCheckService(IOptions<HealthCheckService.Options> options,
    ILogger<HealthCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int port = options.Value.Port;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        logger.LogInformation("Health check listening on TCP port {Port}", port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                client.Close();
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public class Options
    {
        public const string SECTION_NAME = "HealthCheck";

        public int Port { get; set; }
    }
}
