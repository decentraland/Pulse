using System.Numerics;
using System.Runtime.CompilerServices;
using Pulse.Peers;
using System.Collections.Concurrent;

namespace Pulse.InterestManagement;

public sealed class SpatialGrid(float cellSize)
{
    private readonly float inverseCellSize = 1f / cellSize;

    private readonly ConcurrentDictionary<long, HashSet<PeerIndex>> cells = new ();
    private readonly ConcurrentDictionary<PeerIndex, long> peersByKey = new ();

    public void Set(PeerIndex peer, Vector3 position)
    {
        long key = ComputeKey(position);

        if (!cells.ContainsKey(key))
            cells.TryAdd(key, new HashSet<PeerIndex>());

        // No need to move the peer if already exists in the same cell
        if (!cells[key].Add(peer)) return;

        // Move the peer into the new cell
        if (peersByKey.TryGetValue(peer, out long prevKey))
            if (prevKey != key)
                cells[prevKey].Remove(peer);

        peersByKey[peer] = key;
    }

    public void Remove(PeerIndex peer)
    {
        if (!peersByKey.Remove(peer, out long key)) return;

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
        ((long)x << 32) | (uint)z;
}
