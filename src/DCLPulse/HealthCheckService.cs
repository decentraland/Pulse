using System.Net;
using System.Net.Sockets;

namespace Pulse;

public class HealthCheckService(ILogger<HealthCheckService> logger) : BackgroundService
{
    private const int PORT = 8080;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, PORT);
        listener.Start();
        logger.LogInformation("Health check listening on TCP port {Port}", PORT);

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
}
