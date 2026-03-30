using System.Numerics;

namespace Pulse.Peers.Simulation;

public class TeleportBoard(int maxPeers)
{
    private readonly TeleportEntry?[] teleports = new TeleportEntry?[maxPeers];

    public void Publish(PeerIndex peerId, Vector3 position, uint serverTick) =>
        teleports[peerId] = new TeleportEntry(position, serverTick);

    public bool TryRead(PeerIndex peerId, out TeleportEntry entry)
    {
        entry = default;
        if (teleports[peerId] == null) return false;
        entry = teleports[peerId]!.Value;
        return true;
    }

    public void Remove(PeerIndex peerId) =>
        teleports[peerId] = null;
}

public readonly record struct TeleportEntry(Vector3 Position, uint ServerTick);