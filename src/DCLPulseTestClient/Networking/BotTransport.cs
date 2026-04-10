using Google.Protobuf;
using Pulse.Transport;

namespace PulseTestClient.Networking;

public sealed class BotTransport(
    ENetTransport sharedTransport,
    MessagePipe pipe) : ITransport
{
    private PeerId peerId;

    public async Task ConnectAsync(string address, int port, CancellationToken ct)
    {
        peerId = await sharedTransport.ConnectPeerAsync(address, port, pipe, ct);
    }

    public Task DisconnectAsync(DisconnectReason reason, CancellationToken ct)
    {
        sharedTransport.DisconnectPeer(peerId, reason);
        return Task.CompletedTask;
    }

    public void Send(IMessage message, PacketMode mode)
    {
        // Not used — PulseMultiplayerService sends via MessagePipe directly.
        // Kept to satisfy ITransport interface.
    }

    public void Dispose()
    {
        // Shared transport owns the lifecycle.
    }
}
