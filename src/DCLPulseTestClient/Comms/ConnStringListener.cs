using Decentraland.Kernel.Comms.V3;
using Position = Decentraland.Common.Position;

namespace PulseTestClient.Comms;

/// <summary>
///     An island assignment as ws-connector reported it. <see cref="ConnStr" /> is the LiveKit
///     connection string this harness exists to observe.
/// </summary>
public sealed record IslandChange(
    string IslandId,
    string ConnStr,
    string? FromIslandId,
    IReadOnlyDictionary<string, Position> Peers);

/// <summary>
///     Steady-state half of the session: drains <see cref="ServerPacket" />s and republishes island
///     changes. Runs until cancelled, until the session dies, or until the server kicks us.
/// </summary>
public sealed class ConnStringListener(ICommsConnection connection)
{
    public event Action<IslandChange>? IslandChanged;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ServerPacket packet = await connection.ReceiveAsync(ct);

            switch (packet.MessageCase)
            {
                case ServerPacket.MessageOneofCase.IslandChanged:
                    IslandChangedMessage message = packet.IslandChanged;
                    string? fromIsland = message.HasFromIslandId ? message.FromIslandId : null;

                    // Redacted: a real gatekeeper's conn string carries a signed LiveKit JWT, and this
                    // line is the one place the harness would otherwise print it.
                    Console.WriteLine($"[ws-connector] Island {message.IslandId} (from {fromIsland ?? "none"}), {message.Peers.Count} peer(s), connStr {ConnStringRedaction.Redact(message.ConnStr)}");

                    // Copy the peer map — the packet is transient and handlers may outlive this iteration.
                    IslandChanged?.Invoke(new IslandChange(
                        message.IslandId,
                        message.ConnStr,
                        fromIsland,
                        new Dictionary<string, Position>(message.Peers)));

                    break;

                case ServerPacket.MessageOneofCase.Kicked:
                    // ws-connector reuses KR_NEW_SESSION for platform bans as well, so the reason alone
                    // does not say whether another session took the wallet or the wallet was refused.
                    throw new PulseException($"ws-connector kicked the session: {packet.Kicked.Reason}.");
            }
        }
    }
}
