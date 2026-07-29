using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using Pulse.Peers;

namespace Pulse.InterestManagement;

/// <summary>
///     The set of live <see cref="SpatialGrid" />s, one per realm, and the router that keeps each peer
///     in exactly one of them. Realm isolation is structural: a grid holds one realm's peers and
///     nothing else, so neither interest management nor cluster derivation ever compares realms.
///     <para />
///     Per-peer bookkeeping (which realm and cell a slot currently occupies) lives here rather than in
///     each grid. Realm names arrive from clients, so a per-grid array indexed by
///     <see cref="PeerIndex" /> would let a peer mint an arbitrary number of full-size arrays by
///     teleporting to fresh realm names. Here the cost is one pair of arrays regardless of realm count,
///     and a grid is dropped once its last occupant leaves — bounding live grids by connected peers.
///     <para />
///     Reads are lock-free. Writes take one lock shared by every realm, which keeps the write path's
///     contention identical to a single global grid while the read path gains the partition.
/// </summary>
public sealed class RealmSpatialGrids(float cellSize, int maxPeers)
{
    private readonly float inverseCellSize = 1f / cellSize;

    private readonly ConcurrentDictionary<string, SpatialGrid> gridsByRealm = new (StringComparer.Ordinal);

    // Where each peer slot currently sits. A null realm means the peer occupies no grid, which is
    // also the only state in which its cell key is meaningless. A slot is touched only by the worker
    // that owns the peer, so these are single-reader as well as single-writer.
    private readonly string?[] peerRealms = new string?[maxPeers];
    private readonly long[] peerCellKeys = new long[maxPeers];

    private readonly Lock writeLock = new ();

    /// <summary>
    ///     Places a peer at a position within a realm, moving it out of its previous cell — and out of
    ///     its previous realm's grid — if it had one. Creates the realm's grid on its first occupant.
    ///     <para />
    ///     Single-writer per peer slot: only the worker that owns the peer may call this for it.
    /// </summary>
    public void Set(PeerIndex peer, string realm, Vector3 position)
    {
        var slot = (int)peer.Value;
        long key = ComputeKey(position);

        string? prevRealm = Volatile.Read(ref peerRealms[slot]);
        long prevKey = Volatile.Read(ref peerCellKeys[slot]);

        if (prevKey == key && string.Equals(prevRealm, realm, StringComparison.Ordinal)) return;

        lock (writeLock)
        {
            // Read-then-add rather than GetOrAdd: the value overload evaluates eagerly, so it would
            // build a grid — and the ConcurrentDictionary inside it — on every publish that lands in a
            // realm that already exists. Safe under the lock, which is the only writer.
            if (!gridsByRealm.TryGetValue(realm, out SpatialGrid? grid))
            {
                grid = new SpatialGrid();
                gridsByRealm[realm] = grid;
            }

            // Added before the old location is vacated. At every instant in between, the peer is in the
            // new cell's published set or still in the old one — never in neither — and a solo peer
            // changing cells cannot empty, and so evict, the very grid it is moving within. Note this
            // is an instantaneous property: a reader walking several cells in sequence can still miss a
            // peer that moves between two of its reads.
            grid.Add(peer, key);

            if (prevRealm is not null)
                RemoveFromRealm(prevRealm, peer, prevKey);

            Volatile.Write(ref peerRealms[slot], realm);
            Volatile.Write(ref peerCellKeys[slot], key);
        }
    }

    /// <summary>
    ///     Removes a peer from whichever realm and cell it occupies. Idempotent — a peer that was never
    ///     placed, or was already removed, is a no-op.
    /// </summary>
    public void Remove(PeerIndex peer)
    {
        var slot = (int)peer.Value;
        string? realm = Volatile.Read(ref peerRealms[slot]);

        if (realm is null) return;

        lock (writeLock)
        {
            long key = Volatile.Read(ref peerCellKeys[slot]);

            Volatile.Write(ref peerRealms[slot], null);

            RemoveFromRealm(realm, peer, key);
        }
    }

    /// <summary>
    ///     The grid holding <paramref name="realm" />'s peers, or null when no peer occupies that realm
    ///     — which is also the answer for a null realm, so an unplaced peer and an empty realm resolve
    ///     alike.
    ///     <para />
    ///     The grid returned may be evicted straight afterwards, since a grid is dropped once its last
    ///     occupant leaves. An evicted grid stays readable and reports whatever was left in it, so a
    ///     stale reference degrades to a possibly-empty view rather than to an error.
    /// </summary>
    public SpatialGrid? GetGrid(string? realm)
    {
        if (realm is null) return null;

        // TryGetValue rather than the GetValueOrDefault extension, for the reason on GetPeers.
        gridsByRealm.TryGetValue(realm, out SpatialGrid? grid);

        return grid;
    }

    /// <summary>
    ///     Enumerates the realms that currently hold peers, with their grids. Lock-free and weakly
    ///     consistent: a realm created or dropped mid-enumeration may or may not be observed.
    /// </summary>
    public RealmGridEnumerator GetRealmGrids() =>
        new (gridsByRealm.GetEnumerator());

    /// <summary>
    ///     The cell coordinates containing a world position. Neighbour cells are reached by stepping
    ///     these coordinates, which lands on exactly the intended cell — unlike offsetting the position
    ///     by a cell size and re-flooring, where a position near a boundary can round back into its own
    ///     cell.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CellCoords(Vector3 position, out int x, out int z)
    {
        x = CellCoord(position.X);
        z = CellCoord(position.Z);
    }

    private void RemoveFromRealm(string realm, PeerIndex peer, long cellKey)
    {
        if (!gridsByRealm.TryGetValue(realm, out SpatialGrid? grid)) return;

        grid.Remove(peer, cellKey);

        // A realm nobody occupies keeps no grid — realm names are client-supplied, so retaining one
        // per name ever seen would grow without bound.
        if (grid.IsEmpty)
            gridsByRealm.TryRemove(realm, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ComputeKey(Vector3 position) =>
        SpatialGrid.PackKey(CellCoord(position.X), CellCoord(position.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellCoord(float v) =>
        (int)MathF.Floor(v * inverseCellSize);

    /// <summary>
    ///     One realm and the grid holding its peers.
    /// </summary>
    public readonly record struct RealmGrid(string Realm, SpatialGrid Grid);

    /// <summary>
    ///     Allocation-free-per-item enumerator over the live realm grids.
    /// </summary>
    public struct RealmGridEnumerator(IEnumerator<KeyValuePair<string, SpatialGrid>> inner)
    {
        public RealmGrid Current => new (inner.Current.Key, inner.Current.Value);

        public bool MoveNext() =>
            inner.MoveNext();

        public RealmGridEnumerator GetEnumerator() =>
            this;
    }
}
