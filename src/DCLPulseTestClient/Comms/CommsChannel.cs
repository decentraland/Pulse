using PulseTestClient.Auth;
using System.Numerics;

namespace PulseTestClient.Comms;

/// <summary>
///     One bot's ws-connector session, from handshake to steady state. Owns the socket, the sign
///     flow, the island listener and the heartbeat pump so the bot lifecycle deals with a single
///     object.
/// </summary>
/// <remarks>
///     This is a second, independent failure domain: the Pulse session is what the bot exists to
///     exercise, and losing the observation channel must not take it down. <see cref="RunAsync" />
///     therefore never throws — every failure is reported on the <c>[comms]</c> prefix and ends only
///     this channel.
/// </remarks>
public sealed class CommsChannel
{
    private readonly ClientOptions options;
    private readonly IAuthenticator authenticator;
    private readonly string account;
    private readonly string walletAddress;
    private readonly Func<Vector3> position;

    // Created in RunAsync when --join-livekit is set, null otherwise. A field so the finally block can
    // leave the room even when the channel ends by exception.
    private LiveKitJoiner? joiner;

    /// <summary>Raised for every island assignment observed on this channel.</summary>
    public event Action<IslandChange>? IslandChanged;

    public CommsChannel(
        ClientOptions options,
        IAuthenticator authenticator,
        string account,
        string walletAddress,
        Func<Vector3> position)
    {
        this.options = options;
        this.authenticator = authenticator;
        this.account = account;
        this.walletAddress = walletAddress;
        this.position = position;
    }

    /// <summary>
    ///     Runs the channel until cancelled or until it fails. Never throws.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var connection = new WebSocketCommsConnection();

        try
        {
            var signFlow = new ArchipelagoSignFlow(connection, authenticator, account);
            await signFlow.ConnectAsync(options.CommsUrl, walletAddress, ct);

            var listener = new ConnStringListener(connection);

            joiner = options.JoinLiveKit ? new LiveKitJoiner(account) : null;

            listener.IslandChanged += change =>
            {
                IslandChanged?.Invoke(change);

                // Fire and forget: a join takes seconds, and the listener loop must keep draining or
                // the heartbeat and the next assignment stall behind it. JoinAsync never throws.
                if (joiner is not null)
                    _ = joiner.JoinAsync(change.ConnStr, change.IslandId, ct);
            };

            var pump = new HeartbeatPump(connection, position);

            // Whichever stops first ends the channel: the listener returning means the session died,
            // and the pump returning means keepalive stopped, which the server answers by closing.
            await Task.WhenAny(listener.RunAsync(ct), pump.RunAsync(ct)).Unwrap();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a fault.
        }
        catch (Exception e)
        {
            // Distinct prefix and no rethrow: the Pulse session on this account keeps running, and the
            // run's outcome must not read as a transport failure when only the observation channel went.
            Console.WriteLine($"[comms] [{account}] channel failed: {e.Message}");
        }
        finally
        {
            if (joiner is not null)
                await joiner.DisposeAsync();

            await connection.DisposeAsync();
        }
    }
}
