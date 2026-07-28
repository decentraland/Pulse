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

    /// <summary>
    ///     Enumerates the currently occupied cells with their occupants. Lock-free — it neither
    ///     takes <c>writeLock</c> nor blocks <see cref="Set" />/<see cref="Remove" />.
    ///     <para />
    ///     Weakly consistent, like any <see cref="ConcurrentDictionary{TKey,TValue}" /> enumeration:
    ///     cells added or removed while enumerating may or may not be observed. Every cell that is
    ///     observed is internally consistent, because occupant sets are copy-on-write.
    /// </summary>
    public OccupiedCellEnumerator GetOccupiedCells() =>
        new (cells.GetEnumerator());

    /// <summary>
    ///     Splits a packed cell key back into its cell coordinates. Inverse of the internal packing
    ///     used by <see cref="GetOccupiedCells" />, so callers can walk cell neighborhoods.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UnpackKey(long key, out int x, out int z)
    {
        x = (int)(key >> (sizeof(int) * 8));
        z = (int)(uint)key;
    }

    /// <summary>
    ///     Packs cell coordinates into the key used by <see cref="GetOccupiedCells" />. Exposed so
    ///     callers can address a neighboring cell without re-deriving the encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long PackKey(int x, int z) =>
        ((long)x << (sizeof(int) * 8)) | (uint)z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ComputeKey(Vector3 position) =>
        PackKey(CellCoord(position.X), CellCoord(position.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellCoord(float v) =>
        (int)MathF.Floor(v * inverseCellSize);

    /// <summary>
    ///     One occupied cell as observed by <see cref="GetOccupiedCells" />.
    ///     <para />
    ///     <see cref="Occupants" /> must be treated as read-only. It is safe to hold and iterate
    ///     without locking because <see cref="Set" /> and <see cref="Remove" /> replace the set
    ///     instance rather than mutating it, so an observed reference never changes underneath the
    ///     reader. Mutating it would corrupt the grid for every other reader.
    /// </summary>
    public readonly record struct OccupiedCell(long Key, HashSet<PeerIndex> Occupants);

    /// <summary>
    ///     Allocation-free-per-item enumerator over occupied cells. Mirrors the shape of
    ///     <see cref="Peers.Simulation.SnapshotBoard.ActivePeerEnumerator" /> so both boards read
    ///     the same way at the call site.
    /// </summary>
    public struct OccupiedCellEnumerator(IEnumerator<KeyValuePair<long, HashSet<PeerIndex>>> inner)
    {
        public OccupiedCell Current => new (inner.Current.Key, inner.Current.Value);

        public bool MoveNext() =>
            inner.MoveNext();

        public OccupiedCellEnumerator GetEnumerator() =>
            this;
    }
}
