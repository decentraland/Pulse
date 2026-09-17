using Microsoft.Extensions.Options;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Peers;

namespace Pulse.Presence;

/// <summary>
///     Derives the <c>engine.parcel_changes</c> feed (iteration-2 C1) from the clustering pass. Holds
///     one slot per <see cref="PeerIndex" /> with the last <c>(address, realm, parcel)</c> published
///     for it, and emits two kinds of entry: a peer whose <c>(realm, parcel)</c> differs from its slot
///     — a peer with no slot has changed by definition, which is what makes a first placement an entry
///     (C1.1) — and a departure, from <see cref="OnPeerRemoved" /> alone (C1.2).
///     <para />
///     Slots are per connection but the feed is <b>per wallet</b>, so both kinds reduce per address
///     (A1, C1.3). A duplicate-session kick or fast reconnect puts one wallet on two slots until the
///     evicted connection's transport disconnect lands (its ENet disconnect is queued, so up to
///     <c>Transport:PeerTimeoutMs</c>) plus <c>Peers:DisconnectionCleanTimeoutMs</c> for the cleanup
///     that clears the slot.
///     <para />
///     Exits come only from the <c>PeerSimulation</c> cleanup, never from "present last pass, missing
///     in this one" — every way a peer can leave ends in that one cleanup, so deriving them from the
///     pass as well would emit each twice.
///     <para />
///     Threading: passes run on the <see cref="ClusterTracker" /> thread and cleanup on a peer worker,
///     so both take <see cref="stateLock" />. Held across the publish, which makes "read slot, publish,
///     write slot" atomic per peer.
/// </summary>
public sealed class ParcelChangeTracker
{
    private readonly IClusterFeedPublisher feed;
    private readonly ParcelEncoder parcelEncoder;
    private readonly bool enabled;

    private readonly Lock stateLock = new ();
    private readonly Slot[] slots;

    // Occupied slots, so a snapshot's list is sized once instead of grown.
    private int liveCount;

    // Recency for the per-wallet reduction only. ulong at one pass per second, so neither can wrap.
    private ulong passStamp;
    private ulong occupancyStamp;

    public ParcelChangeTracker(
        IClusterFeedPublisher feed,
        ParcelEncoder parcelEncoder,
        IOptions<PresenceOptions> presenceOptions,
        IOptions<NatsOptions> natsOptions,
        int maxPeers)
    {
        this.feed = feed;
        this.parcelEncoder = parcelEncoder;

        // Follows the existing NATS gating: no broker means nothing to publish to. Resolved once —
        // neither option can change after start.
        enabled = presenceOptions.Value.Enabled && natsOptions.Value.IsConfigured;

        slots = new Slot[maxPeers];
    }

    /// <summary>
    ///     Whether this tracker publishes anything. False leaves every entry point a no-op.
    /// </summary>
    public bool Enabled => enabled;

    /// <summary>
    ///     Turns one completed clustering pass into presence entries. Called from the tracker thread
    ///     right after the pass is published, so the feed and the stats surface see the same peers.
    ///     <para />
    ///     Whether the batch must be a snapshot is decided first: a snapshot sends every live peer, so
    ///     the per-peer deltas would be redundant. Slots are brought up to date either way.
    /// </summary>
    public void ObservePass(ClusterPass pass)
    {
        if (!enabled) return;

        lock (stateLock)
        {
            bool snapshot = feed.TryTakeParcelSnapshotRequest(out PresenceSnapshotReason reason);

            passStamp++;

            for (var i = 0; i < pass.Peers.Count; i++)
                Observe(pass.Peers[i], publishChange: !snapshot);

            // An outbox eviction is raised by the deltas just published, so its snapshot request does
            // not exist until the loop above has run. Answering it here rather than next pass is what
            // makes C1.4's "immediately after any outbox eviction" the next batch turn.
            if (!snapshot && feed.TryTakeParcelSnapshotRequest(out PresenceSnapshotReason raisedByThisPass))
            {
                snapshot = true;
                reason = raisedByThisPass;
            }

            if (snapshot)
                feed.PublishParcelSnapshot(CollectLivePresence(), reason);
        }
    }

    /// <summary>
    ///     The single exit choke point: one <c>parcel</c>-absent entry for a peer that had a published
    ///     presence, nothing for one that never did.
    ///     <para />
    ///     Wallet-scoped (A1) — the entry goes out only once the wallet is on no slot of this server.
    ///     A kick accepts the new connection long before it cleans the evicted slot, so publishing
    ///     this slot's departure would take a peer offline that is online on its newer connection; and
    ///     with both in one batch the per-address coalescing keeps the exit and drops the placement.
    ///     <para />
    ///     Clearing the slot is mandatory, not tidy: <see cref="PeerIndex" /> is recycled, and leftover
    ///     state would make the next wallet's first placement look unchanged.
    /// </summary>
    public void OnPeerRemoved(PeerIndex peer)
    {
        if (!enabled) return;

        var index = (int)peer.Value;

        if (index >= slots.Length) return;

        lock (stateLock)
        {
            ref Slot slot = ref slots[index];

            if (slot.Realm is not { } realm)
            {
                slot = default(Slot);

                return;
            }

            string address = slot.Address!;

            // Cleared before the scan, so the departing slot cannot answer for itself.
            slot = default(Slot);
            liveCount--;

            if (IsPlaced(address)) return;

            feed.PublishParcelChange(address, realm, parcel: null);
        }
    }

    /// <summary>
    ///     Whether any live slot still holds <paramref name="address" />. Linear, under the lock the
    ///     caller already holds: once per disconnect over <c>Transport:MaxPeers</c> entries, so an
    ///     address index would be state to keep consistent for no measurable gain.
    /// </summary>
    private bool IsPlaced(string address)
    {
        for (var index = 0; index < slots.Length; index++)
        {
            Slot slot = slots[index];

            if (slot.Realm is not null && string.Equals(slot.Address, address, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Brings one peer's slot up to date and, when <paramref name="publishChange" /> is set,
    ///     publishes an entry if its <c>(realm, parcel)</c> moved.
    /// </summary>
    private void Observe(ClusterPeerInfo info, bool publishChange)
    {
        var index = (int)info.Peer.Value;

        // Ignored rather than thrown on: an exception here would take the whole pass — and the stats
        // surface with it — down over one peer.
        if (index >= slots.Length) return;

        ref Slot slot = ref slots[index];

        // Before the unchanged early-return below: "still in the pass" must hold for a peer standing
        // still, which is the usual state of the live connection whose duplicate is being cleaned up.
        slot.SeenAtPass = passStamp;

        parcelEncoder.Decode(info.Parcel, out int x, out int z);

        var parcel = new ParcelCoord(x, z);
        string realm = info.Realm;

        // A different wallet on this slot is a new presence wherever it stands — guarding the same
        // aliasing class as PeerSimulation.DetectAndHandleAliasing. Reference equality is the fast
        // path: IdentityBoard hands out one string instance per peer for as long as it lives.
        bool sameWallet = ReferenceEquals(slot.Wallet, info.Wallet)
                       || string.Equals(slot.Wallet, info.Wallet, StringComparison.OrdinalIgnoreCase);

        if (slot.Realm is not null
            && sameWallet
            && slot.Parcel == parcel
            && string.Equals(slot.Realm, realm, StringComparison.Ordinal))
            return;

        // Read before slot.Realm is written below, since that is what marks an empty slot.
        bool acquired = slot.Realm is null || !sameWallet;

        if (slot.Realm is null)
            liveCount++;

        slot.Wallet = info.Wallet;

        // Lowercased once per wallet rather than once per published entry.
        if (!sameWallet)
            slot.Address = CanonicalName.Of(info.Wallet);

        slot.Realm = realm;
        slot.Parcel = parcel;

        // On acquisition only, never on a later move, so the tie-break is recency of the *session*.
        // A kicked connection keeps moving for a client round trip, so a stamp rewritten on every
        // change would be the stale slot's whenever it moved last — and which moved last is decided
        // by grid.GetOccupiedCells() order, which is spatial. Occupancy order is monotone in session
        // age; placement order is not.
        if (acquired)
            slot.OccupiedAt = ++occupancyStamp;

        if (publishChange)
            feed.PublishParcelChange(slot.Address!, realm, parcel);
    }

    /// <summary>
    ///     Every wallet with a published presence — the whole of what a snapshot carries — reduced
    ///     per address, not per slot (C1.3). Naming one wallet twice would leave a last-write-wins
    ///     consumer holding whichever entry happened to sort last, and would stop the batch order
    ///     being a function of its content, since two entries for one address tie under the
    ///     publisher's address-only comparator.
    /// </summary>
    private List<PeerPresence> CollectLivePresence()
    {
        var presence = new List<PeerPresence>(liveCount);
        var entryByAddress = new Dictionary<string, (int Entry, int Slot)>(liveCount, StringComparer.Ordinal);

        for (var index = 0; index < slots.Length; index++)
        {
            Slot slot = slots[index];

            if (slot.Realm is not { } realm) continue;

            string address = slot.Address!;
            var entry = new PeerPresence(address, realm, slot.Parcel);

            if (!entryByAddress.TryGetValue(address, out (int Entry, int Slot) held))
            {
                entryByAddress[address] = (presence.Count, index);
                presence.Add(entry);

                continue;
            }

            if (!Supersedes(slot, slots[held.Slot])) continue;

            // Replaced in place; the publisher sorts by address anyway.
            presence[held.Entry] = entry;
            entryByAddress[address] = (held.Entry, index);
        }

        return presence;
    }

    /// <summary>
    ///     Which of two slots holding one wallet is that wallet's presence. The slot the latest pass
    ///     saw wins: <c>ClusterTracker</c> collects only the wallet's live binding, so a kicked
    ///     connection stops being observed once the handshake rebinds the wallet.
    ///     <para />
    ///     Both seen in one pass means a single traversal straddled that rebind — the weakly-consistent
    ///     grid read reaching the kicked connection's cell before it and the incoming one's cell after.
    ///     There the newer <b>occupancy</b> wins. Occupancy, not placement: the kicked connection is
    ///     still on the wire and is often the one that moved most recently, so placement recency would
    ///     name the parcel the player just left.
    /// </summary>
    private static bool Supersedes(in Slot candidate, in Slot held) =>
        candidate.SeenAtPass != held.SeenAtPass
            ? candidate.SeenAtPass > held.SeenAtPass
            : candidate.OccupiedAt > held.OccupiedAt;

    /// <summary>
    ///     What one peer slot carries between passes. <see cref="Realm" /> doubles as the occupancy
    ///     flag, so an empty slot is <c>default</c>.
    /// </summary>
    private struct Slot
    {
        // The IdentityBoard instance Address was derived from, so the lowercase form is recomputed
        // only when a different wallet lands here.
        public string? Wallet;
        public string? Address;

        public string? Realm;
        public ParcelCoord Parcel;

        // The pass this slot was last observed in, and the order this occupancy was acquired in —
        // not the order its parcel was written in. See Supersedes.
        public ulong SeenAtPass;
        public ulong OccupiedAt;
    }
}
