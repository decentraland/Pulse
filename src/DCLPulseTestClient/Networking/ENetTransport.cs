using ENet;
using System.Collections.Concurrent;
using Google.Protobuf;
using Pulse.Transport;

namespace PulseTestClient.Networking;

public sealed class ENetTransport : IDisposable
{
    private readonly ENetTransportOptions options;
    private readonly byte[] receiveBuffer = new byte[4096];
    private readonly byte[] sendBuffer = new byte[4096];
    private readonly ConcurrentQueue<ConnectRequest> connectQueue = new ();
    private readonly Dictionary<uint, PeerConnection> connections = new ();

    private Host? client;
    private CancellationTokenSource? lifeCycleCts;
    private bool initialized;

    public ENetTransport(ENetTransportOptions options)
    {
        this.options = options;
    }

    public void Dispose()
    {
        lifeCycleCts.SafeCancelAndDispose();
        client?.Flush();
        client?.Dispose();
        client = null;

        if (initialized)
        {
            Library.Deinitialize();
            initialized = false;
        }
    }

    public void Initialize()
    {
        if (initialized) return;

        if (!Library.Initialize())
            throw new InvalidOperationException("ENet library failed to initialize.");

        client = new Host();
        client.Create(peerLimit: options.PeerLimit, channelLimit: ENetChannel.COUNT);
        initialized = true;

        lifeCycleCts = new CancellationTokenSource();
        RunENetThread(lifeCycleCts.Token);
    }

    public Task<PeerId> ConnectPeerAsync(string ip, int port, MessagePipe pipe, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<PeerId>(TaskCreationOptions.RunContinuationsAsynchronously);
        connectQueue.Enqueue(new ConnectRequest(ip, (ushort)port, pipe, tcs));

        return tcs.Task.WaitAsync(ct);
    }

    public void DisconnectPeer(PeerId peerId, DisconnectReason reason)
    {
        if (connections.TryGetValue(peerId.Value, out PeerConnection conn))
            conn.Peer.Disconnect((uint)reason);
    }

    private void RunENetThread(CancellationToken ct)
    {
        Task.Factory.StartNew(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                ProcessConnectRequests();

                if (client == null) continue;

                // Service does socket I/O + returns one event. Short timeout so we never block outgoing flushes.
                if (client.Service(1, out Event netEvent) > 0)
                    HandleEvent(ref netEvent);

                // Service only returns one event per call. If multiple packets arrived in that I/O pass,
                // the rest are queued internally. CheckEvents drains them without redundant socket I/O.
                while (client.CheckEvents(out netEvent) > 0)
                    HandleEvent(ref netEvent);

                SendOutgoingMessages();
            }

            client?.Flush();
            client?.Dispose();
            client = null;
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ProcessConnectRequests()
    {
        while (connectQueue.TryDequeue(out ConnectRequest request))
        {
            var address = new Address();
            address.SetHost(request.Ip);
            address.Port = request.Port;
            Peer peer = client!.Connect(address, channelLimit: ENetChannel.COUNT);
            connections[peer.ID] = new PeerConnection(peer, request.Pipe, request.Completion);
        }
    }

    private void HandleEvent(ref Event netEvent)
    {
        uint peerId = netEvent.Peer.ID;

        switch (netEvent.Type)
        {
            case EventType.Connect:
                if (connections.TryGetValue(peerId, out PeerConnection conn))
                {
                    connections[peerId] = conn with { Peer = netEvent.Peer };
                    conn.Completion?.TrySetResult(new PeerId(peerId));
                }
                break;

            case EventType.Disconnect:
            case EventType.Timeout:
                connections.Remove(peerId);
                break;

            case EventType.Receive:
            {
                using Packet packet = netEvent.Packet;
                packet.CopyTo(receiveBuffer);

                if (connections.TryGetValue(peerId, out PeerConnection recvConn))
                {
                    recvConn.Pipe.OnDataReceived(new MessagePacket<Packet>(
                        packet,
                        new ReadOnlySpan<byte>(receiveBuffer, 0, packet.Length),
                        new PeerId(peerId)));
                }

                break;
            }
        }
    }

    private void SendOutgoingMessages()
    {
        foreach ((uint _, PeerConnection conn) in connections)
        {
            while (conn.Pipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg))
            {
                ENetChannel channel = ToENetChannel(msg.PacketMode);
                SendToPeer(conn.Peer, channel, msg.Message);
            }
        }
    }

    private void SendToPeer(Peer peer, ENetChannel channel, IMessage message)
    {
        var size = message.CalculateSize();
        var span = new Span<byte>(sendBuffer, 0, size);
        message.WriteTo(span);
        var packet = default(Packet);
        packet.Create(span, channel.PacketMode);
        peer.Send(channel.ChannelId, ref packet);
    }

    private static ENetChannel ToENetChannel(PacketMode mode)
    {
        return mode switch
        {
            PacketMode.RELIABLE => ENetChannel.RELIABLE,
            PacketMode.UNRELIABLE_SEQUENCED => ENetChannel.UNRELIABLE_SEQUENCED,
            _ => ENetChannel.UNRELIABLE_UNSEQUENCED,
        };
    }

    private readonly record struct ConnectRequest(string Ip, ushort Port, MessagePipe Pipe, TaskCompletionSource<PeerId> Completion);
    private record struct PeerConnection(Peer Peer, MessagePipe Pipe, TaskCompletionSource<PeerId>? Completion);
}
