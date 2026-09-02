using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Pulse.Peers;

namespace Pulse.InterestManagement;

/// <summary>
///     Cell-to-occupants index for a single realm. One instance exists per realm that currently holds
///     at least one peer; <see cref="RealmSpatialGrids" /> creates them, routes writes to them, and
///     drops them when they empty. A grid therefore only ever contains same-realm peers, which is
///     what lets both interest management and cluster derivation work without a realm predicate.
///     <para />
///     Reads are lock-free. Occupant sets are copy-on-write: <see cref="Add" /> and
///     <see cref="Remove" /> replace the set instance rather than mutating it, so a reader that
///     already holds a reference keeps iterating a consistent snapshot of that cell.
///     <para />
///     Writes are not internally synchronized — every mutation runs under the owner's write lock.
/// </summary>
public sealed class SpatialGrid
{
    private readonly ConcurrentDictionary<long, HashSet<PeerIndex>> cells = new ();

    // Occupied-cell count, maintained here rather than asked of the dictionary. A
    // ConcurrentDictionary answers IsEmpty by acquiring every one of its internal locks whenever it
    // really is empty — precisely the case the eviction check exists to detect — and Count does so
    // unconditionally. Mutated and read under the owner's write lock, so a plain field suffices.
    private int occupiedCells;

    internal bool IsEmpty =>
        occupiedCells == 0;

    /// <summary>
    ///     Splits a packed cell key back into its cell coordinates. Inverse of <see cref="PackKey" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UnpackKey(long key, out int x, out int z)
    {
        x = (int)(key >> (sizeof(int) * 8));
        z = (int)(uint)key;
    }

    /// <summary>
    ///     Packs cell coordinates into the key used by <see cref="GetPeers" /> and carried by
    ///     <see cref="OccupiedCell" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long PackKey(int x, int z) =>
        ((long)x << (sizeof(int) * 8)) | (uint)z;

    /// <summary>
    ///     The occupants of one cell, or null when the cell is empty. The returned set is read-only to
    ///     the caller — mutating it would corrupt the grid for every other reader.
    /// </summary>
    public HashSet<PeerIndex>? GetPeers(long cellKey)
    {
        // TryGetValue rather than the GetValueOrDefault extension, which is declared on
        // IReadOnlyDictionary and so reaches the same lookup through a non-inlinable interface call.
        // Worth only a few ns per neighborhood scan — a realistic scan is dominated by cache misses
        // across the bucket table, which both spellings pay alike — so this is for the native API and
        // for consistency with the rest of the codebase, not for the timing.
        cells.TryGetValue(cellKey, out HashSet<PeerIndex>? peers);

        return peers;
    }

    /// <summary>
    ///     Enumerates the currently occupied cells with their occupants. Lock-free, and therefore
    ///     weakly consistent like any <see cref="ConcurrentDictionary{TKey,TValue}" /> enumeration:
    ///     cells added or removed mid-enumeration may or may not be observed, but every cell that is
    ///     observed is internally consistent because occupant sets are copy-on-write.
    /// </summary>
    public OccupiedCellEnumerator GetOccupiedCells() =>
        new (cells.GetEnumerator());

    /// <summary>
    ///     Adds a peer to a cell. Caller must hold <see cref="RealmSpatialGrids" />' write lock.
    /// </summary>
    internal void Add(PeerIndex peer, long cellKey)
    {
        if (cells.TryGetValue(cellKey, out HashSet<PeerIndex>? existing))
        {
            cells[cellKey] = new HashSet<PeerIndex>(existing) { peer };
            return;
        }

        cells[cellKey] = [peer];
        occupiedCells++;
    }

    /// <summary>
    ///     Removes a peer from a cell, dropping the cell once it is empty. Caller must hold
    ///     <see cref="RealmSpatialGrids" />' write lock.
    /// </summary>
    internal void Remove(PeerIndex peer, long cellKey)
    {
        if (!cells.TryGetValue(cellKey, out HashSet<PeerIndex>? existing)) return;

        HashSet<PeerIndex> without = new (existing);

        if (!without.Remove(peer)) return;

        if (without.Count > 0)
        {
            cells[cellKey] = without;
            return;
        }

        // TryRemove, not the IDictionary Remove extension, which looks the key up and then removes it:
        // a non-atomic read-then-write against a concurrent collection, safe here only because the
        // owner's lock serializes writers. The two measure the same; this one cannot become a race if
        // that lock is ever relaxed.
        cells.TryRemove(cellKey, out _);
        occupiedCells--;
    }

    /// <summary>
    ///     One occupied cell as observed by <see cref="GetOccupiedCells" />. <see cref="Occupants" />
    ///     is read-only for the same reason as <see cref="GetPeers" />'s result.
    /// </summary>
    public readonly record struct OccupiedCell(long Key, HashSet<PeerIndex> Occupants);

    /// <summary>
    ///     Allocation-free-per-item enumerator over occupied cells.
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
