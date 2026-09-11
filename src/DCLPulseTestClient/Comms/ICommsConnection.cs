using Decentraland.Kernel.Comms.V3;

namespace PulseTestClient.Comms;

/// <summary>
///     One ws-connector session. Whole <see cref="ClientPacket" />s go out and whole
///     <see cref="ServerPacket" />s come back — the WebSocket message boundary is the framing,
///     so nothing here adds a length prefix. An instance covers a single session; reconnecting
///     means building a new one.
/// </summary>
public interface ICommsConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <param name="url">Full ws-connector endpoint, including the path — e.g. <c>ws://localhost:5000/ws</c>.</param>
    Task ConnectAsync(string url, CancellationToken ct);

    Task SendAsync(ClientPacket packet, CancellationToken ct);

    /// <summary>
    ///     The next packet the server sent, or a <see cref="PulseException" /> once the session has ended.
    ///     ws-connector refuses a peer (deny list, ban, bad signature, wrong stage) by simply closing the
    ///     socket, so "the connection died" is a first-class result here and must never read as
    ///     "nothing to receive yet".
    /// </summary>
    ValueTask<ServerPacket> ReceiveAsync(CancellationToken ct);

    Task DisconnectAsync(CancellationToken ct);
}
