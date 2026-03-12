using System.Numerics;
using System.Runtime.CompilerServices;
using Pulse.Peers;
using System.Collections.Concurrent;

namespace Pulse.InterestManagement;

public sealed class SpatialGrid(float cellSize, int maxPeers)
{
    // No playable area can reach that coordinate.
    private const long NO_CELL = long.MinValue;

    private readonly float inverseCellSize = 1f / cellSize;

    private readonly ConcurrentDictionary<long, HashSet<PeerIndex>> cells = new ();
    private readonly long[] peerCellKeys = InitKeys(maxPeers);

    private static long[] InitKeys(int count)
    {
        var keys = new long[count];
        Array.Fill(keys, NO_CELL);
        return keys;
    }

    public void Set(PeerIndex peer, Vector3 position)
    {
        long key = ComputeKey(position);

        if (!cells.ContainsKey(key))
            cells.TryAdd(key, new HashSet<PeerIndex>());

        // No need to move the peer if already exists in the same cell
        if (!cells[key].Add(peer)) return;

        // Move the peer into the new cell
        long prevKey = Volatile.Read(ref peerCellKeys[peer.Value]);

        if (prevKey != NO_CELL && prevKey != key)
            cells[prevKey].Remove(peer);

        Volatile.Write(ref peerCellKeys[peer.Value], key);
    }

    public void Remove(PeerIndex peer)
    {
        long key = Volatile.Read(ref peerCellKeys[peer.Value]);
        if (key == NO_CELL) return;

        Volatile.Write(ref peerCellKeys[peer.Value], NO_CELL);

        if (!cells.TryGetValue(key, out HashSet<PeerIndex>? peers)) return;
        if (!peers.Remove(peer)) return;

        if (peers.Count == 0)
            cells.Remove(key, out _);
    }

    public HashSet<PeerIndex>? GetPeers(Vector3 position) =>
        cells.GetValueOrDefault(ComputeKey(position));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ComputeKey(Vector3 position) =>
        PackKey(CellCoord(position.X), CellCoord(position.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellCoord(float v) =>
        (int)MathF.Floor(v * inverseCellSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackKey(int x, int z) =>
        ((long)x << (sizeof(int) * 8)) | (uint)z;
}
