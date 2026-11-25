using ENet;
using Pulse.Transport;
using Host = ENet.Host;

namespace DCLPulseTests;

/// <summary>
///     Pins the behaviour the Pulse server needs from the native ENet library it loads: the MTU
///     the client announces, large reliable packets, and the dotted IPv4 peer address. Uses a real
///     loopback host pair so the library under test is exercised through the C# wrapper.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ENetNativeLibraryTests
{
    private const int LARGE_PACKET_LENGTH = 3000;

    /// <summary>
    ///     Above the 8192-byte limit the native library applies to a listening host.
    /// </summary>
    private const int OVERSIZED_PACKET_LENGTH = 9000;

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
    public void ClientAnnouncesMtu1392_AndServerGrantsIt()
    {
        using var server = new Host();
        using var client = new Host();
        ushort port = CreateHosts(server, client);

        Connect(client, port);
        Peer serverSidePeer = DriveUntilServerSeesConnect(server, client);

        Assert.That(serverSidePeer.MTU, Is.EqualTo(1392u));
    }

    [Test]
    [CancelAfter(5000)]
    public void Reliable3000BytePacket_ArrivesIntact()
    {
        using var server = new Host();
        using var client = new Host();
        ushort port = CreateHosts(server, client);

        Peer clientSidePeer = Connect(client, port);
        DriveUntilServerSeesConnect(server, client);
        DriveUntilConnected(server, client, clientSidePeer);

        var data = new byte[LARGE_PACKET_LENGTH];

        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);

        var packet = default(Packet);
        packet.Create(data, PacketFlags.Reliable);
        Assert.That(clientSidePeer.Send(0, ref packet), Is.Zero, "Client failed to queue the packet.");

        Event receive = DriveUntilServerSeesReceive(server, client);

        using Packet received = receive.Packet;
        var copy = new byte[received.Length];

        if (received.Length == LARGE_PACKET_LENGTH)
            received.CopyTo(copy);

        Assert.That(received.Length, Is.EqualTo(LARGE_PACKET_LENGTH));
        Assert.That(copy, Is.EqualTo(data));
    }

    [Test]
    [CancelAfter(5000)]
    public void PeerIp_IsDottedIPv4()
    {
        using var server = new Host();
        using var client = new Host();
        ushort port = CreateHosts(server, client);

        Connect(client, port);
        Peer serverSidePeer = DriveUntilServerSeesConnect(server, client);

        Assert.That(serverSidePeer.IP, Is.EqualTo("127.0.0.1"));
    }

    [Test]
    [CancelAfter(5000)]
    public void OversizedPacket_DisconnectsTheClientWithPacketCorrupted()
    {
        using var server = new Host();
        using var client = new Host();
        ushort port = CreateHosts(server, client);

        Peer clientSidePeer = Connect(client, port);
        DriveUntilServerSeesConnect(server, client);
        DriveUntilConnected(server, client, clientSidePeer);

        var packet = default(Packet);
        packet.Create(new byte[OVERSIZED_PACKET_LENGTH], PacketFlags.Reliable);
        Assert.That(clientSidePeer.Send(0, ref packet), Is.Zero, "Client failed to queue the packet.");

        EventType? serverEvent = null;
        uint? clientDisconnectData = null;
        long deadline = Environment.TickCount64 + 3000;

        while (Environment.TickCount64 < deadline && (serverEvent == null || clientDisconnectData == null))
        {
            if (client.Service(1, out Event clientEvent) > 0 && clientEvent.Type == EventType.Disconnect)
                clientDisconnectData = clientEvent.Data;

            if (server.Service(1, out Event serverSideEvent) > 0 && serverEvent == null)
            {
                serverEvent = serverSideEvent.Type;

                if (serverSideEvent.Type == EventType.Receive)
                    serverSideEvent.Packet.Dispose();
            }
        }

        Assert.That(serverEvent, Is.EqualTo(EventType.Disconnect), "The server's next peer event was not a disconnect.");
        Assert.That(clientDisconnectData, Is.EqualTo((uint)DisconnectReason.PACKET_CORRUPTED));
    }

    private static ushort CreateHosts(Host server, Host client)
    {
        ushort port = AllocatePort();

        var serverAddr = new Address();
        serverAddr.SetIP("0.0.0.0");
        serverAddr.Port = port;
        server.Create(serverAddr, peerLimit: 4, channelLimit: 3);

        client.Create(null, peerLimit: 1, channelLimit: 3);

        return port;
    }

    private static Peer Connect(Host client, ushort port)
    {
        var connectAddr = new Address();
        connectAddr.SetIP("127.0.0.1");
        connectAddr.Port = port;

        return client.Connect(connectAddr, channelLimit: 3, data: 0);
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
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);

        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));

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

    private static void DriveUntilConnected(Host server, Host client, Peer clientSidePeer)
    {
        long deadline = Environment.TickCount64 + 2000;

        while (Environment.TickCount64 < deadline)
        {
            if (clientSidePeer.State == PeerState.Connected)
                return;

            client.Service(1, out _);
            server.Service(1, out _);
        }

        Assert.Fail("Client never reached the Connected state within 2s.");
    }

    private static Event DriveUntilServerSeesReceive(Host server, Host client)
    {
        long deadline = Environment.TickCount64 + 2000;

        while (Environment.TickCount64 < deadline)
        {
            client.Service(1, out _);

            if (server.Service(1, out Event ev) > 0 && ev.Type == EventType.Receive)
                return ev;
        }

        Assert.Fail("Server never observed a Receive event within 2s.");
        return default(Event);
    }
}
