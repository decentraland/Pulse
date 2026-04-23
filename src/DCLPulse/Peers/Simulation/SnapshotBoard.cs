using System.Runtime.CompilerServices;

namespace Pulse.Peers.Simulation;

/// <summary>
///     Shared snapshot store with per-peer rolling history. Pre-allocated flat arrays indexed
///     by <see cref="PeerIndex" />, which is a zero-based index, always in [0, maxPeers).
///     <para />
///     Each peer slot holds a ring buffer of recent snapshots (for targeted RESYNC deltas)
///     and tracks the latest sequence number.
///     <para />
///     Writers: only the owning worker writes to a given slot — single writer per slot.
///     Readers: any worker during simulation — multiple concurrent readers.
///     Thread safety: seqlock per slot — zero allocation, zero contention, truly lock-free.
/// </summary>
public sealed class SnapshotBoard
{
    private readonly int ringCapacity;

    // Per-peer ring buffers: rings[peerIndex] is a fixed-size circular array of snapshots.
    private readonly PeerSnapshot[][] rings;

    // Seqlock per peer: odd = write in progress, even = stable.
    private readonly uint[] versions;

    // Latest stored sequence number per peer.
    private readonly uint[] lastSeqs;

    private readonly bool[] active;

    public SnapshotBoard(int maxPeers, int ringCapacity)
    {
        this.ringCapacity = ringCapacity;

        rings = new PeerSnapshot[maxPeers][];
        versions = new uint[maxPeers];
        lastSeqs = new uint[maxPeers];
        active = new bool[maxPeers];

        Array.Fill(lastSeqs, uint.MaxValue);

        for (var i = 0; i < maxPeers; i++)
            rings[i] = new PeerSnapshot[ringCapacity];
    }

    /// <summary>
    ///     Publish a snapshot for a peer. Stores it in the ring at <c>snapshot.Seq % ringCapacity</c>
    ///     and updates <see cref="LastSeq" />.
    ///     Called only by the owning worker — single writer per slot.
    ///     <para />
    ///     Emote-ledger invariant: if the incoming snapshot has no emote intent (<c>Emote == null</c>),
    ///     we carry the active emote forward from the previous snapshot so every ring slot reflects
    ///     the current emote state. A stop marker in the previous slot is consumed (resolves to
    ///     <c>null</c>) so post-stop snapshots are correctly idle. Consequence: the latest snapshot
    ///     is self-sufficient for "is this peer emoting, since when, for how long" — independent of
    ///     ring depth. See <see cref="PeerSnapshotExtensions.IsEmoting" />.
    ///     <para />
    ///     Realm-ledger invariant: same carry-forward pattern. <see cref="TeleportHandler" /> is
    ///     the only publisher that sets <c>snapshot.Realm</c> explicitly; every other publish
    ///     inherits the previous snapshot's realm so AoI lookups read the latest ring slot and
    ///     never see a null realm after the peer has teleported once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(PeerIndex id, in PeerSnapshot snapshot)
    {
        var index = (int)id.Value;

        // Ledger inheritance for fields the caller didn't set explicitly.
        PeerSnapshot toWrite = snapshot with
        {
            Emote = snapshot.Emote ?? InheritEmoteState(index),
            Realm = snapshot.Realm ?? InheritRealm(index),
        };

        // Increment to odd (write in progress)
        Volatile.Write(ref versions[index], Volatile.Read(ref versions[index]) + 1);

        rings[index][toWrite.Seq % ringCapacity] = toWrite;
        lastSeqs[index] = toWrite.Seq;

        // Full fence: ensure data writes are globally visible before the version goes even.
        // Volatile.Write alone only prevents reordering of that store with later stores —
        // on ARM, preceding stores (rings, lastSeqs) could still be reordered past it.
        Thread.MemoryBarrier();

        // Increment to even (write complete)
        Volatile.Write(ref versions[index], Volatile.Read(ref versions[index]) + 1);
    }

    /// <summary>
    ///     Resolve the emote state for a non-event snapshot from the previous ring slot.
    ///     Stop markers are transient — they only appear on their own snapshot and are consumed
    ///     by the next publish (resolves to null). Active emotes are carried forward verbatim.
    ///     Called on the owner worker; no seqlock needed (single writer reads its own prior slot).
    /// </summary>
    private EmoteState? InheritEmoteState(int index)
    {
        uint lastSeq = lastSeqs[index];

        if (lastSeq == uint.MaxValue)
            return null;

        EmoteState? prev = rings[index][lastSeq % ringCapacity].Emote;

        return prev is { StopReason: not null } ? null : prev;
    }

    /// <summary>
    ///     Resolve the realm for a non-teleport publish from the previous ring slot. Returns null
    ///     when the peer has never been assigned a realm (no prior snapshot, or all prior snapshots
    ///     are themselves null-realm). TeleportHandler is the only publisher that writes a non-null
    ///     realm; this carry-forward makes every subsequent ring slot self-sufficient for AoI.
    /// </summary>
    private string? InheritRealm(int index)
    {
        uint lastSeq = lastSeqs[index];

        if (lastSeq == uint.MaxValue)
            return null;

        return rings[index][lastSeq % ringCapacity].Realm;
    }

    /// <summary>
    ///     Read the latest sequence number for a peer.
    ///     Safe to call from the owning worker without seqlock (single writer).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint LastSeq(PeerIndex id) =>
        Volatile.Read(ref lastSeqs[(int)id.Value]);

    /// <summary>
    ///     Read the latest snapshot for a peer. Called by any worker during simulation.
    ///     Spins briefly on seqlock contention (the write is a ~80-byte struct copy — nanoseconds).
    ///     Returns false only if the slot is inactive.
    /// </summary>
    public bool TryRead(PeerIndex id, out PeerSnapshot snapshot) =>
        SpinRead((int)id.Value, requestedSeq: null, out snapshot);

    /// <summary>
    ///     Read a specific historical snapshot by sequence number.
    ///     Returns false if the seq has been overwritten (ring wrapped) or the slot is inactive.
    ///     Used for RESYNC_REQUEST — targeted delta from an older baseline instead of STATE_FULL.
    /// </summary>
    public bool TryRead(PeerIndex id, uint seq, out PeerSnapshot snapshot) =>
        SpinRead((int)id.Value, seq, out snapshot);

    /// <summary>
    ///     Seqlock-protected read of a ring slot. Spins on contention.
    ///     When <paramref name="requestedSeq" /> is null, reads the latest snapshot (seq resolved from lastSeqs).
    ///     When non-null, reads the specific historical seq and verifies it hasn't been overwritten.
    ///     <para />
    ///     Seqlock over a per-slot lock because:
    ///     - Multiple readers proceed in parallel — no reader-reader contention when all workers
    ///     read the same popular subject during the same simulation tick.
    ///     - Writer never waits for readers — input processing is never stalled by simulation reads.
    ///     - Contention is near-zero (nanosecond write window vs 33-100ms write interval),
    ///     so the spin-retry path almost never fires.
    /// </summary>
    private bool SpinRead(int index, uint? requestedSeq, out PeerSnapshot snapshot)
    {
        if (!Volatile.Read(ref active[index]))
        {
            snapshot = default(PeerSnapshot);
            return false;
        }

        var spin = new SpinWait();

        while (true)
        {
            uint v1 = Volatile.Read(ref versions[index]);

            if ((v1 & 1) != 0)
            {
                spin.SpinOnce();
                continue;
            }

            uint seq = requestedSeq ?? Volatile.Read(ref lastSeqs[index]);
            snapshot = rings[index][seq % ringCapacity];

            if (Volatile.Read(ref versions[index]) == v1)

                // For latest reads, always valid. For historical reads, verify ring hasn't wrapped.
                return !requestedSeq.HasValue || snapshot.Seq == requestedSeq.Value;

            spin.SpinOnce();
        }
    }

    /// <summary>
    ///     Returns true if the peer currently has an active emote. Ring-wrap safe because
    ///     <see cref="Publish" /> carries the active emote forward onto every snapshot.
    /// </summary>
    public bool IsEmoting(PeerIndex id) =>
        TryRead(id, out PeerSnapshot snapshot) && snapshot.IsEmoting();

    public void SetActive(PeerIndex id)
    {
        Volatile.Write(ref active[(int)id.Value], true);
    }

    /// <summary>
    ///     It should be called when the Peer disconnects/about to disconnect
    /// </summary>
    public void ClearActive(PeerIndex id)
    {
        var index = (int)id.Value;
        Volatile.Write(ref active[index], false);
        lastSeqs[index] = uint.MaxValue;
        Array.Clear(rings[index]);
    }

    /// <summary>
    ///     Enumerates active peer indices. Snapshot of active state at call time — no consistency
    ///     guarantee with concurrent SetActive/ClearActive calls.
    /// </summary>
    public ActivePeerEnumerator GetActivePeers() =>
        new (active, active.Length);

    public struct ActivePeerEnumerator
    {
        private readonly bool[] active;
        private readonly int capacity;
        private int index;

        internal ActivePeerEnumerator(bool[] active, int capacity)
        {
            this.active = active;
            this.capacity = capacity;
            index = -1;
        }

        public PeerIndex Current => new ((uint)index);

        public bool MoveNext()
        {
            while (++index < capacity)
            {
                if (Volatile.Read(ref active[index]))
                    return true;
            }

            return false;
        }

        public ActivePeerEnumerator GetEnumerator() =>
            this;
    }
}
