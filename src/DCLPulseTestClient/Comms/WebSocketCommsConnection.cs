using Decentraland.Kernel.Comms.V3;
using Google.Protobuf;
using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace PulseTestClient.Comms;

/// <summary>
///     <see cref="ICommsConnection" /> over <see cref="ClientWebSocket" />. A background loop receives
///     binary frames, reassembles fragmented messages, parses them and hands the packets to an unbounded
///     channel that <see cref="ReceiveAsync" /> drains, so the caller never blocks the socket.
/// </summary>
public sealed class WebSocketCommsConnection : ICommsConnection
{
    private const int RECEIVE_CHUNK_BYTES = 16 * 1024;

    // Reassembly cap. Nothing ws-connector sends comes close; without it a peer that never sets
    // EndOfMessage grows the buffer until the process dies, with no packet ever to point at.
    private const int MAX_MESSAGE_BYTES = 1024 * 1024;

    private readonly Channel<ServerPacket> inbound = Channel.CreateUnbounded<ServerPacket>(
        new UnboundedChannelOptions {SingleWriter = true, SingleReader = true});

    // ClientWebSocket faults on overlapping sends, and the heartbeat pump ticks independently of
    // whatever else the session decides to write.
    private readonly SemaphoreSlim sendLock = new (1, 1);

    private ClientWebSocket? socket;
    private CancellationTokenSource? loopCts;
    private Task? receiveLoop;
    private string closeReason = "it was never opened";

    public bool IsConnected => socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string url, CancellationToken ct)
    {
        if (socket is not null)
            throw new PulseException($"{nameof(WebSocketCommsConnection)} handles one session only — create a new instance to reconnect.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("ws" or "wss"))
            throw new PulseException($"'{url}' is not a ws:// or wss:// URL.");

        var connecting = new ClientWebSocket();

        try
        {
            await connecting.ConnectAsync(uri, ct);
        }
        catch (Exception e)
        {
            connecting.Dispose();
            throw new PulseException($"Cannot connect to ws-connector at {url}: {e.Message}");
        }

        socket = connecting;

        // Deliberately not linked to ct: callers pass short-lived per-stage handshake tokens, and the
        // receive loop has to outlive all of them. Only DisconnectAsync/DisposeAsync end it.
        loopCts = new CancellationTokenSource();
        receiveLoop = ReceiveLoopAsync(connecting, loopCts.Token);
    }

    public async Task SendAsync(ClientPacket packet, CancellationToken ct)
    {
        ClientWebSocket ws = socket ?? throw new PulseException($"Cannot send {packet.MessageCase}: the ws-connector session was never opened.");

        if (ws.State != WebSocketState.Open)
            throw new PulseException($"Cannot send {packet.MessageCase}: the ws-connector session is {ws.State}.");

        byte[] payload = packet.ToByteArray();

        await sendLock.WaitAsync(ct);

        try
        {
            await ws.SendAsync(payload, WebSocketMessageType.Binary, true, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new PulseException($"Cannot send {packet.MessageCase} to ws-connector: {e.Message}");
        }
        finally { sendLock.Release(); }
    }

    public async ValueTask<ServerPacket> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await inbound.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            throw new PulseException($"The ws-connector session ended: {closeReason}.");
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        closeReason = "it was closed locally";

        if (socket is {State: WebSocketState.Open})
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ws-connector] Close handshake failed: {e.Message}");
            }
        }

        await StopLoopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        closeReason = "it was disposed";

        await StopLoopAsync();

        socket?.Dispose();
        socket = null;
        sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(RECEIVE_CHUNK_BYTES);
        var message = new ArrayBufferWriter<byte>(RECEIVE_CHUNK_BYTES);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closeReason = $"the server closed the socket ({ws.CloseStatus?.ToString() ?? "no status"}: {ws.CloseStatusDescription ?? "no description"})";
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    closeReason = $"the server sent a {result.MessageType} frame, but the protocol is binary only";
                    return;
                }

                if (message.WrittenCount + result.Count > MAX_MESSAGE_BYTES)
                {
                    closeReason = $"a message exceeded the {MAX_MESSAGE_BYTES} B reassembly limit";
                    return;
                }

                message.Write(chunk.AsSpan(0, result.Count));

                // One ServerPacket per WebSocket message, but a message may still arrive split across
                // frames — parsing before the last one would fail on a perfectly healthy connection.
                if (!result.EndOfMessage)
                    continue;

                ServerPacket packet;

                try
                {
                    packet = ServerPacket.Parser.ParseFrom(message.WrittenSpan);
                }
                catch (InvalidProtocolBufferException e)
                {
                    closeReason = $"a {message.WrittenCount} B message did not decode as a ServerPacket: {e.Message}";
                    return;
                }

                message.ResetWrittenCount();
                inbound.Writer.TryWrite(packet);
            }
        }
        catch (OperationCanceledException)
        {
            // Local shutdown; closeReason was already set by whoever cancelled.
        }
        catch (Exception e)
        {
            closeReason = $"receiving failed: {e.Message}";
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);

            // Completing the channel is what turns a dead socket into an error at the read site.
            inbound.Writer.TryComplete();
        }
    }

    private async Task StopLoopAsync()
    {
        if (loopCts is null)
            return;

        await loopCts.CancelAsync();

        if (receiveLoop is not null)
        {
            // The loop handles its own failures, but teardown must not throw on the caller's behalf.
            try
            {
                await receiveLoop;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ws-connector] Receive loop ended with: {e.Message}");
            }
        }

        loopCts.Dispose();
        loopCts = null;
    }
}
