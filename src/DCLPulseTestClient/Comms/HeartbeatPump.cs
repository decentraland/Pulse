using Decentraland.Kernel.Comms.V3;
using System.Numerics;
using Position = Decentraland.Common.Position;

namespace PulseTestClient.Comms;

/// <summary>
///     Keeps the ws-connector session alive and tells archipelago where the bot is. The socket's idle
///     timeout is 90 s, and archipelago only assigns an island once it has a position for the peer —
///     so this pump is what makes an <c>IslandChanged</c> arrive at all.
/// </summary>
public sealed class HeartbeatPump
{
    private static readonly TimeSpan DEFAULT_INTERVAL = TimeSpan.FromSeconds(30);

    private readonly ICommsConnection connection;
    private readonly Func<Vector3> position;
    private readonly TimeSpan interval;

    /// <param name="position">Read on every tick — the bot moves, so a snapshot would pin it in place.</param>
    /// <param name="interval">Defaults to 30 s, well inside the server's 90 s idle timeout.</param>
    public HeartbeatPump(ICommsConnection connection, Func<Vector3> position, TimeSpan? interval = null)
    {
        this.connection = connection;
        this.position = position;
        this.interval = interval ?? DEFAULT_INTERVAL;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Beat once up front: archipelago has no position for this peer until it does, so waiting a
        // full interval only delays the island assignment we are here to observe.
        await SendAsync(ct);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(ct))
            await SendAsync(ct);
    }

    private Task SendAsync(CancellationToken ct)
    {
        Vector3 current = position();

        return connection.SendAsync(new ClientPacket
        {
            Heartbeat = new Heartbeat
            {
                Position = new Position {X = current.X, Y = current.Y, Z = current.Z},
            },
        }, ct);
    }
}
