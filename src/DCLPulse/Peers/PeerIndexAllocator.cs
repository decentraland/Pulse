using Pulse.Transport;

namespace Pulse.Peers;

/// <summary>
///     Server-allocated <see cref="PeerIndex" /> pool, decoupled from the ENet transport slot.
///     <para />
///     Lifecycle is driven by the simulation, not a time-based drain:
///     <list type="number">
///         <item>
///             <b>Allocate</b>: <see cref="TryAllocate" /> pops a fresh slot on ENet connect.
///         </item>
///         <item>
///             <b>Mark pending</b>: the transport calls <see cref="MarkPending" /> on ENet
///             disconnect. The slot is no longer in the free-list, so no subsequent
///             <see cref="TryAllocate" /> will reissue it.
///         </item>
///         <item>
///             <b>Release</b>: <see cref="Release" /> is called from
///             <c>PeerSimulation.CleanupDisconnectedPeer</c> — the single worker-thread point at
///             which every per-peer board (snapshot, identity, profile, spatial, observer views)
///             is wiped for this <see cref="PeerIndex" />. Only then does the slot return to the
///             free-list. This keeps the allocator in perfect lockstep with the simulation's
///             cleanup: a slot is reusable exactly when its per-peer state has been cleared.
///         </item>
///     </list>
///     <para />
///     Both disconnect-after-auth and auth-timeout paths funnel through the same sequence —
///     ENet Disconnect → <see cref="MarkPending" /> → simulation transitions to DISCONNECTING →
///     <c>DisconnectionCleanTimeoutMs</c> elapses → <c>CleanupDisconnectedPeer</c> →
///     <see cref="Release" />. There is no independent clock on the allocator side, so the two
///     cannot drift.
/// </summary>
public interface IPeerIndexAllocator
{
    /// <summary>
    ///     Pop a free slot, stamped with <paramref name="transport" /> as its owner. Stamping here —
    ///     the single point at which a slot acquires its transport — makes the owner derivable from the
    ///     index and misroute structurally impossible: no caller can forget to stamp. A recycled slot is
    ///     re-stamped with the new owner and never carries the previous owner's transport.
    /// </summary>
    public bool TryAllocate(TransportId transport, out PeerIndex peerIndex);

    /// <summary>
    ///     Park the slot — it will not be handed out by <see cref="TryAllocate" /> until
    ///     <see cref="Release" /> is called. Idempotent. Called from the transport on ENet
    ///     disconnect before <c>OnPeerDisconnected</c> is pushed onto the incoming channel,
    ///     so the pending state is in effect before the simulation observes the disconnect.
    /// </summary>
    public void MarkPending(PeerIndex peerIndex);

    /// <summary>
    ///     Return the slot to the free-list. Call site: <c>CleanupDisconnectedPeer</c>, after
    ///     every per-peer board has been wiped. No-op if the slot wasn't pending (e.g. a
    ///     cleanup fires for a peer whose disconnect event never produced a <c>MarkPending</c>,
    ///     which shouldn't happen but is handled defensively).
    /// </summary>
    public void Release(PeerIndex peerIndex);
}

public sealed class PeerIndexAllocator : IPeerIndexAllocator
{
    private readonly Queue<PeerIndex> freeList;
    private readonly HashSet<PeerIndex> pending = new ();
    private readonly Lock syncRoot = new ();

    public int FreeCount
    {
        get
        {
            lock (syncRoot) return freeList.Count;
        }
    }

    public int PendingCount
    {
        get
        {
            lock (syncRoot) return pending.Count;
        }
    }

    public PeerIndexAllocator(int maxPeers)
    {
        freeList = new Queue<PeerIndex>(maxPeers);

        for (var i = 0; i < maxPeers; i++)
            freeList.Enqueue(new PeerIndex((uint)i));
    }

    public bool TryAllocate(TransportId transport, out PeerIndex peerIndex)
    {
        lock (syncRoot)
        {
            if (freeList.Count == 0)
            {
                peerIndex = default(PeerIndex);
                return false;
            }

            // Stamp the owning transport as the slot is handed out. The free-list stores canonical
            // (value-only) indices; this is the sole point a slot acquires its transport, so a
            // reissued slot is always re-stamped with the new owner.
            peerIndex = new PeerIndex(freeList.Dequeue().Value, transport);
            return true;
        }
    }

    public void MarkPending(PeerIndex peerIndex)
    {
        lock (syncRoot) pending.Add(peerIndex);
    }

    public void Release(PeerIndex peerIndex)
    {
        lock (syncRoot)
        {
            if (pending.Remove(peerIndex))
                freeList.Enqueue(peerIndex);
        }
    }
}
