using DCL.WebTransport;
using Google.Protobuf;
using Pulse.Transport;
using Pulse.Transport.WebTransport;

namespace PulseTestClient.Networking;

/// <summary>
///     Per-bot WebTransport transport — the browser-equivalent counterpart to <see cref="ENetTransport" />,
///     driven by the same rust-web-transport library via <see cref="WebTransportClient" />. Owns one QUIC
///     session and a service/drain thread that bridges the bot's <see cref="MessagePipe" />, applying the
///     client side of Pulse's channel semantics with the shared codecs: outbound reliable → a length-framed
///     stream; unreliable → a {channelId, seq} datagram; inbound stream → reassembled frames; inbound
///     datagram → a bare <c>ServerMessage</c> (the server sends those without a header).
/// </summary>
public sealed class WebTransportBotTransport(MessagePipe pipe, byte[]? serverCertHash) : ITransport
{
    // Datagram channel ids — must match WebTransportHostedService on the server.
    private const byte CHANNEL_SEQUENCED = 1;
    private const byte CHANNEL_UNSEQUENCED = 2;
    private const int MAX_MESSAGE_BYTES = 4096;

    private readonly StreamFrameReader streamReader = new (MAX_MESSAGE_BYTES);
    private readonly DatagramSequencer sequencer = new ();
    private readonly byte[] sendBuffer = new byte[MAX_MESSAGE_BYTES];

    private WebTransportClient? client;
    private CancellationTokenSource? loopCts;

    public void Dispose()
    {
        loopCts?.Cancel();
        loopCts?.Dispose();
        loopCts = null;
    }

    public async Task ConnectAsync(string address, int port, CancellationToken ct)
    {
        string url = $"https://{FormatHost(address)}:{port}/";

        // Connect blocks until the QUIC session is established — keep it off the caller's thread.
        WebTransportClient connected = await Task.Run(() => WebTransportClient.Connect(url, serverCertHash), ct);

        // Task.Run can't abort the in-flight native Connect, so a cancellation that raced it still
        // returns a live session — dispose it rather than leak the native handle.
        if (ct.IsCancellationRequested)
        {
            connected.Dispose();
            ct.ThrowIfCancellationRequested();
        }

        client = connected;
        loopCts = new CancellationTokenSource();
        RunLoop(client, loopCts.Token);
    }

    public Task DisconnectAsync(DisconnectReason reason, CancellationToken ct)
    {
        // Stop the loop; it closes and disposes the session on its own thread, so every native call on
        // the client handle stays single-threaded.
        loopCts?.Cancel();
        return Task.CompletedTask;
    }

    public void Send(IMessage message, PacketMode mode)
    {
        // Not used — PulseMultiplayerService sends via MessagePipe directly (drained by the loop).
    }

    private void RunLoop(WebTransportClient session, CancellationToken ct)
    {
        Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Name ??= "WebTransportClient";

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (session.TryService(1, out WebTransportEvent ev))
                        HandleEvent(ev);

                    FlushOutgoing(session);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception exception)
            {
                Console.WriteLine($"WebTransport client loop failed: {exception}");
            }
            finally
            {
                // Dispose on every exit path so the native session is released even after a fault.
                session.Dispose();
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void HandleEvent(in WebTransportEvent ev)
    {
        switch (ev.Kind)
        {
            case WebTransportEventKind.StreamData:
                try
                {
                    streamReader.Append(ev.Data);

                    while (streamReader.TryRead(out byte[] message))
                        Deliver(message);
                }
                catch (InvalidDataException exception)
                {
                    Console.WriteLine($"WebTransport: oversized stream frame from server — {exception.Message}");
                }

                break;

            case WebTransportEventKind.Datagram:
                // Server→client datagrams are bare ServerMessages (no transport header).
                Deliver(ev.Data);
                break;
        }
    }

    private void FlushOutgoing(WebTransportClient session)
    {
        while (pipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg))
        {
            int size = msg.Message.CalculateSize();

            if (size > sendBuffer.Length)
            {
                Console.WriteLine($"WebTransport: outgoing message ({size} B) exceeds the {sendBuffer.Length} B buffer — dropping.");
                continue;
            }

            var span = new Span<byte>(sendBuffer, 0, size);
            msg.Message.WriteTo(span);

            if (msg.PacketMode == PacketMode.RELIABLE)
            {
                if (!session.SendStream(StreamFraming.Frame(span)))
                    Console.WriteLine("WebTransport: reliable stream send failed.");
            }
            else
            {
                byte channelId = msg.PacketMode == PacketMode.UNRELIABLE_SEQUENCED ? CHANNEL_SEQUENCED : CHANNEL_UNSEQUENCED;

                // A false return also covers a datagram past the transport's size limit — quantized
                // MovementInput stays well under it, so surfacing it is enough without a preflight guard.
                if (!session.SendDatagram(DatagramFraming.Frame(channelId, sequencer.Next(channelId), span)))
                    Console.WriteLine("WebTransport: datagram send failed or exceeded the transport limit.");
            }
        }
    }

    private void Deliver(ReadOnlySpan<byte> messageBytes) =>
        pipe.OnDataReceived(new MessagePacket<NoOpPacket>(default, messageBytes, new PeerId(0)));

    private static string FormatHost(string address) =>
        // Bracket a bare IPv6 literal for the URL authority; leave hostnames / IPv4 as-is.
        address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;

    private readonly struct NoOpPacket : IDisposable
    {
        // WebTransport event data is a managed byte[] the binding already copied out — nothing to free.
        public void Dispose() { }
    }
}
