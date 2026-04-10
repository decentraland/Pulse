using Google.Protobuf;
using Pulse.Transport;

namespace PulseTestClient.Networking;

/// <summary>
///     Abstraction for the transport in case it's used actively from other services
/// </summary>
public interface ITransport : IDisposable
{
    Task ConnectAsync(string address, int port, CancellationToken ct);

    Task DisconnectAsync(DisconnectReason reason, CancellationToken ct);

    void Send(IMessage message, PacketMode mode);
}
