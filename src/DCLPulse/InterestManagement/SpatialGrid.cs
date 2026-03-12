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
    private readonly Lock writeLock = new ();

    private static long[] InitKeys(int count)
    {
        var keys = new long[count];
        Array.Fill(keys, NO_CELL);
        return keys;
    }

    public void Set(PeerIndex peer, Vector3 position)
    {
        long key = ComputeKey(position);
        long prevKey = Volatile.Read(ref peerCellKeys[peer.Value]);

        if (prevKey == key) return;

        lock (writeLock)
        {
            if (prevKey != NO_CELL && cells.TryGetValue(prevKey, out HashSet<PeerIndex>? oldSet))
            {
                HashSet<PeerIndex> without = new(oldSet);
                without.Remove(peer);

                if (without.Count == 0)
                    cells.Remove(prevKey, out _);
                else
                    cells[prevKey] = without;
            }

            HashSet<PeerIndex> newSet = cells.TryGetValue(key, out HashSet<PeerIndex>? existing)
                ? new HashSet<PeerIndex>(existing) { peer }
                : [peer];

            cells[key] = newSet;
            Volatile.Write(ref peerCellKeys[peer.Value], key);
        }
    }

    public void Remove(PeerIndex peer)
    {
        long key = Volatile.Read(ref peerCellKeys[peer.Value]);
        if (key == NO_CELL) return;

        lock (writeLock)
        {
            Volatile.Write(ref peerCellKeys[peer.Value], NO_CELL);

            if (!cells.TryGetValue(key, out HashSet<PeerIndex>? existing)) return;

            HashSet<PeerIndex> without = new(existing);
            if (!without.Remove(peer)) return;

            if (without.Count == 0)
                cells.Remove(key, out _);
            else
                cells[key] = without;
        }
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
