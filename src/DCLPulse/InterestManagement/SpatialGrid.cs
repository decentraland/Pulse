using System.Numerics;
using System.Runtime.CompilerServices;
using Pulse.Peers;

namespace Pulse.InterestManagement;

public sealed class SpatialGrid(float cellSize, int maxPeers)
{
    // Sentinel: long.MinValue == PackKey(int.MinValue, 0), mapping to ~1.07B world units from origin.
    // No playable area can reach that coordinate.
    public const long NO_CELL = long.MinValue;

    private readonly float inverseCellSize = 1f / cellSize;
    private readonly long[] peerCellKeys = InitKeys(maxPeers);

    public int MaxPeers => peerCellKeys.Length;

    private static long[] InitKeys(int count)
    {
        var keys = new long[count];
        Array.Fill(keys, NO_CELL);
        return keys;
    }

    public void Set(PeerIndex peer, Vector3 position) =>
        Volatile.Write(ref peerCellKeys[peer.Value], ComputeKey(position));

    public void Remove(PeerIndex peer) =>
        Volatile.Write(ref peerCellKeys[peer.Value], NO_CELL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadCellKey(PeerIndex peer) =>
        Volatile.Read(ref peerCellKeys[peer.Value]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ComputeCellKey(Vector3 position) =>
        ComputeKey(position);

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
