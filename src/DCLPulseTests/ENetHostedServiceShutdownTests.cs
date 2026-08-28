using ENet;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse.Transport;
using Host = ENet.Host;

namespace DCLPulseTests;

/// <summary>
///     Pins the contract that <see cref="ENetHostedService.ShutdownGracefully" /> sends a
///     <see cref="DisconnectReason.GRACEFUL" /> disconnect that actually reaches connected
///     clients. Uses a real loopback ENet host pair — the only honest way to verify the UDP
///     datagram leaves the process.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ENetHostedServiceShutdownTests
{

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!Library.Initialize())
            throw new InvalidOperationException("ENet library failed to initialize.");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Library.Deinitialize();
    }

    [Test]
    [CancelAfter(5000)]
    public void ShutdownGracefully_DeliversGracefulReasonToClient()
    {
        ushort port = AllocatePort();

        using var server = new Host();
        var serverAddr = new Address();
        serverAddr.SetIP("::");
        serverAddr.Port = port;
        server.Create(serverAddr, peerLimit: 4, channelLimit: 2);

        using var client = new Host();
        client.Create(null, peerLimit: 1, channelLimit: 2);

        var connectAddr = new Address();
        connectAddr.SetIP("127.0.0.1");
        connectAddr.Port = port;
        client.Connect(connectAddr, channelLimit: 2, data: 0);

        Peer serverSidePeer = DriveUntilServerSeesConnect(server, client);

        ENetHostedService.ShutdownGracefully(new[] { serverSidePeer }, Substitute.For<ILogger>());

        Event disconnect = DriveUntilClientSeesDisconnect(client);

        Assert.That(disconnect.Data, Is.EqualTo((uint)DisconnectReason.GRACEFUL),
            "Client's disconnect event must carry the GRACEFUL reason code the server emitted.");
    }

    [Test]
    public void ShutdownGracefully_NoOp_WhenNoPeers()
    {
        // Must not throw and must not log — the zero-peer guard is the hot path when a task
        // is recycled before any client has connected.
        Assert.DoesNotThrow(() =>
            ENetHostedService.ShutdownGracefully(Array.Empty<Peer>(), Substitute.For<ILogger>()));
    }

    /// <summary>
    ///     Asks the OS for a free UDP port instead of hardcoding a range. A fixed range is not
    ///     safe on Windows: Docker Desktop's port forwarder holds wide UDP ranges that appear in
    ///     neither <c>netstat</c> nor the excluded-port list, so a hardcoded port fails to bind
    ///     with no visible owner.
    /// </summary>
    private static ushort AllocatePort()
    {
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetworkV6,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);

        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Any, 0));

        if (probe.LocalEndPoint is not System.Net.IPEndPoint bound)
            throw new InvalidOperationException("UDP probe socket reported no local endpoint.");

        return (ushort)bound.Port;
    }

    /// <summary>
    ///     Polls both hosts until the server's Service loop reports a Connect event, then
    ///     returns the peer handle the server would have stored in <c>connectedPeers</c>.
    /// </summary>
    private static Peer DriveUntilServerSeesConnect(Host server, Host client)
    {
        long deadline = Environment.TickCount64 + 2000;

        while (Environment.TickCount64 < deadline)
        {
            // Drive the client first so its CONNECT request goes out on the wire.
            client.Service(1, out _);

            if (server.Service(1, out Event ev) > 0 && ev.Type == EventType.Connect)
                return ev.Peer;
        }

        Assert.Fail("Server never observed a Connect event from the client within 2s.");
        return default(Peer);
    }

    private static Event DriveUntilClientSeesDisconnect(Host client)
    {
        long deadline = Environment.TickCount64 + 2000;

        while (Environment.TickCount64 < deadline)
        {
            if (client.Service(1, out Event ev) > 0 && ev.Type == EventType.Disconnect)
                return ev;
        }

        Assert.Fail("Client never received a Disconnect event within 2s.");
        return default(Event);
    }
}
