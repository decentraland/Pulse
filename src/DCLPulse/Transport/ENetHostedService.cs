using ENet;
using Microsoft.Extensions.Options;
using Pulse.Messaging;
using Pulse.Peers;
using System.Runtime.InteropServices;
using Host = ENet.Host;

namespace Pulse.Transport;

public sealed class ENetHostedService(
    IOptions<ENetTransportOptions> options,
    ILogger<ENetHostedService> logger,
    MessagePipe messagePipe
) : BackgroundService
{
    private readonly ENetTransportOptions options = options.Value;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Library.Initialize())
            throw new InvalidOperationException("ENet library failed to initialize.");

        logger.LogInformation("ENet initialized (version {Version}).", Library.version);
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ENet must be driven on a single dedicated thread — LongRunning prevents
        // the thread-pool from treating this as a short-lived async continuation.
        return Task.Factory.StartNew(
            () => RunLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunLoop(CancellationToken stoppingToken)
    {
        using var host = new Host();

        var address = new Address();
        address.SetIP("0.0.0.0");
        address.Port = options.Port;

        host.Create(address, options.MaxPeers, channelLimit: ENetChannel.COUNT);

        logger.LogInformation("ENet host listening on 0.0.0.0:{Port} (maxPeers={MaxPeers}).",
            options.Port, options.MaxPeers);

        while (!stoppingToken.IsCancellationRequested)
        {
            var polled = false;

            while (!polled)
            {
                if (host.CheckEvents(out Event netEvent) <= 0)
                {
                    if (host.Service(options.ServiceTimeoutMs, out netEvent) <= 0)
                        break;

                    polled = true;
                }

                HandleEvent(ref netEvent);
            }
        }

        host.Flush();
    }

    private void HandleEvent(ref Event netEvent)
    {
        switch (netEvent.Type)
        {
            case EventType.Connect:

                logger.LogDebug("Peer connected: {IP}:{Port} (id={ID}).",
                    netEvent.Peer.IP, netEvent.Peer.Port, netEvent.Peer.ID);

                break;

            case EventType.Disconnect:
                logger.LogDebug("Peer disconnected: id={ID} data={Data}.",
                    netEvent.Peer.ID, netEvent.Data);

                break;

            case EventType.Timeout:
                logger.LogDebug("Peer timed out: id={ID}.", netEvent.Peer.ID);
                break;

            case EventType.Receive:
            {
                using Packet _ = netEvent.Packet;

                unsafe
                {
                    // Parse packet
                    // ENet is not thread-safe, so this callback is always invoked on the main thread

                    messagePipe.OnDataReceived(new MessagePacket<Packet>(
                        netEvent.Packet,
                        new ReadOnlySpan<byte>((void*)netEvent.Packet.NativeData, netEvent.Packet.Length),
                        new PeerId(netEvent.Peer.ID)));

                    break;
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Library.Deinitialize();
        logger.LogInformation("ENet deinitialized.");
    }
}
