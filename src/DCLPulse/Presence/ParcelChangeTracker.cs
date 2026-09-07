using Microsoft.Extensions.Options;
using Pulse.Clusters;
using Pulse.InterestManagement;
using Pulse.Peers;

namespace Pulse.Presence;

/// <summary>
///     Derives the <c>engine.parcel_changes</c> feed (iteration-2 C1) from what the clustering pass
///     already knows. It holds one slot per <see cref="PeerIndex" /> carrying the last
///     <c>(address, realm, parcel)</c> published for that peer, and turns the pass into the two kinds
///     of entry the contract defines:
///     <list type="bullet">
///         <item>
///             a peer whose <c>(realm, parcel)</c> differs from its slot — including a peer with no
///             slot at all, whose "change" is from nowhere, which is what makes a first placement an
///             entry even for a peer that never moves again (C1.1);
///         </item>
///         <item>
///             a peer leaving, published by <see cref="OnPeerRemoved" /> alone (C1.2) and only when
///             the wallet is left on no connection of this server (A1).
///         </item>
///     </list>
///     <para />
///     Exits are deliberately <b>not</b> derived from "present last pass, missing in this one".
///     Every way a peer can leave — clean disconnect, auth/idle timeout, duplicate-session kick,
///     <c>BanEnforcer</c> eviction, <c>PeerDefense</c> kick — ends in the same
///     <c>PeerSimulation</c> cleanup that wipes <c>IdentityBoard</c> and <c>ProfileBoard</c>, so that
///     one call site covers all of them; deriving exits from the pass as well would emit each one
///     twice. It also fixes the ordering: cleanup runs a full
///     <c>Peers:DisconnectionCleanTimeoutMs</c> after the peer left the spatial grid, so no pass that
///     was in flight when it disconnected can still be holding a stale read and republish it as
///     present after the exit went out.
///     <para />
///     Threading: the pass runs on the <see cref="ClusterTracker" /> thread and cleanup on a peer
///     worker, so both entry points take <see cref="stateLock" />. Contention is negligible — one
///     pass per second against one call per disconnect — and holding it across the publish is what
///     makes "read the slot, publish, write the slot" atomic per peer.
/// </summary>
public sealed class ParcelChangeTracker
{
    private readonly IClusterFeedPublisher feed;
    private readonly ParcelEncoder parcelEncoder;
    private readonly bool enabled;

    private readonly Lock stateLock = new ();
    private readonly Slot[] slots;

    // Slots currently holding a published presence, so a snapshot list is sized once instead of
    // grown, and an idle server does not walk the whole table to find nothing.
    private int liveCount;

    public ParcelChangeTracker(
        IClusterFeedPublisher feed,
        ParcelEncoder parcelEncoder,
        IOptions<PresenceOptions> presenceOptions,
        IOptions<NatsOptions> natsOptions,
        int maxPeers)
    {
        this.feed = feed;
        this.parcelEncoder = parcelEncoder;

        // The feed follows the existing NATS gating on purpose: with no broker configured there is
        // nothing to publish to, and a deploy that changes no configuration must behave exactly as it
        // does today. Resolved once — neither option can change after start.
        enabled = presenceOptions.Value.Enabled && natsOptions.Value.IsConfigured;

        slots = new Slot[maxPeers];
    }

    /// <summary>
    ///     Whether this tracker publishes anything at all. False leaves every entry point a no-op,
    ///     which is the state of a deployment with no broker or with <c>Presence:Enabled</c> off.
    /// </summary>
    public bool Enabled => enabled;

    /// <summary>
    ///     Turns one completed clustering pass into presence entries. Called from the tracker thread
    ///     immediately after the pass is published, so the feed is derived from the same peer set the
    ///     stats surface serves.
    ///     <para />
    ///     The publisher is asked first whether the batch has to be a snapshot, because that decides
    ///     what this pass emits: on a snapshot every live peer goes out as one message and the
    ///     per-peer deltas would be redundant; otherwise only what changed does. Either way every
    ///     slot is brought up to date, so the next pass diffs against what consumers now hold.
    /// </summary>
    public void ObservePass(ClusterPass pass)
    {
        if (!enabled) return;

        lock (stateLock)
        {
            bool snapshot = feed.TryTakeParcelSnapshotRequest(out PresenceSnapshotReason reason);

            for (var i = 0; i < pass.Peers.Count; i++)
                Observe(pass.Peers[i], publishChange: !snapshot);

            // An outbox eviction is raised by the deltas this pass has just published, so the
            // request for the snapshot that repairs it does not exist until the loop above has run.
            // Answering it here rather than leaving it for the next pass is what makes C1.4's
            // "immediately after any outbox eviction" the very next batch turn instead of the turn
            // after the pass after it — the state is right here, and one pass later it would already
            // have moved on.
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
    ///     presence, and nothing at all for one that never did — a peer that timed out in
    ///     <c>PENDING_AUTH</c> was never on the feed, so it has no departure to announce.
    ///     <para />
    ///     The entry is <b>wallet-scoped</b> (A1): it goes out only when the wallet is no longer
    ///     placed on any connection of this server. A duplicate-session kick accepts the new
    ///     connection at once and cleans the evicted slot a full
    ///     <c>Peers:DisconnectionCleanTimeoutMs</c> later, so by the time this runs the wallet is
    ///     usually already standing somewhere on a newer slot — and consumers key presence by wallet,
    ///     so publishing this slot's departure would take a peer that is online offline until the next
    ///     snapshot. Worse, when both entries fall in one batch the per-address coalescing keeps the
    ///     later one, so the exit wins and the new placement is never seen at all. The newer placement
    ///     is this wallet's whole presence; the stale slot leaving is not news.
    ///     <para />
    ///     Clearing the slot is mandatory rather than tidy: <see cref="PeerIndex" /> is a recycled
    ///     transport slot, and state left behind would make the next wallet's first placement look
    ///     unchanged — the one case C1.1 exists to rule out.
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
    ///     Whether any live slot still holds <paramref name="address" />. A linear walk of the slot
    ///     table under <see cref="stateLock" />, which is what the caller already holds: it runs once
    ///     per disconnect against <c>Transport:MaxPeers</c> entries, so an index keyed by address
    ///     would be state to keep consistent for no measurable gain.
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
    ///     publishes the entry if its <c>(realm, parcel)</c> moved. A peer with no slot yet has
    ///     changed by definition: its previous state is "nowhere".
    /// </summary>
    private void Observe(ClusterPeerInfo info, bool publishChange)
    {
        var index = (int)info.Peer.Value;

        // Bounds-checked like OnPeerRemoved. A PeerIndex at or past Transport:MaxPeers is a peer this
        // tracker never sized a slot for, and ignoring it beats throwing on the ClusterTracker
        // thread, where the exception would take the whole clustering pass — and the stats surface
        // with it — down over one peer.
        if (index >= slots.Length) return;

        ref Slot slot = ref slots[index];

        parcelEncoder.Decode(info.Parcel, out int x, out int z);

        var parcel = new ParcelCoord(x, z);
        string realm = info.Realm;

        // A different wallet on this slot is a new presence wherever it stands. PeerIndex is a
        // recycled transport slot; nothing releases one today except OnPeerRemoved, which clears the
        // slot — but the codebase already guards the same class of aliasing for the observer view
        // (PeerSimulation.DetectAndHandleAliasing), and a slot reused without that clearing would
        // otherwise publish nothing and leave the next snapshot reporting the wallet that left as
        // standing at the new occupant's parcel, since Address is only refreshed on a change.
        //
        // The reference check is the fast path — IdentityBoard hands out one string instance per peer
        // for as long as it lives — and the value compare runs only when the instance differs, which
        // is once per identification.
        bool sameWallet = ReferenceEquals(slot.Wallet, info.Wallet)
                       || string.Equals(slot.Wallet, info.Wallet, StringComparison.OrdinalIgnoreCase);

        if (slot.Realm is not null
            && sameWallet
            && slot.Parcel == parcel
            && string.Equals(slot.Realm, realm, StringComparison.Ordinal))
            return;

        if (slot.Realm is null)
            liveCount++;

        slot.Wallet = info.Wallet;

        // The lowercase form is computed once per wallet rather than once per published entry.
        if (!sameWallet)
            slot.Address = CanonicalName.Of(info.Wallet);

        slot.Realm = realm;
        slot.Parcel = parcel;

        if (publishChange)
            feed.PublishParcelChange(slot.Address!, realm, parcel);
    }

    /// <summary>
    ///     Every peer with a published presence, which is the whole of what a snapshot carries: one
    ///     non-null entry per active peer with a known realm and parcel.
    /// </summary>
    private List<PeerPresence> CollectLivePresence()
    {
        var presence = new List<PeerPresence>(liveCount);

        for (var index = 0; index < slots.Length; index++)
        {
            Slot slot = slots[index];

            if (slot.Realm is { } realm)
                presence.Add(new PeerPresence(slot.Address!, realm, slot.Parcel));
        }

        return presence;
    }

    /// <summary>
    ///     What one peer slot carries between passes. <see cref="Realm" /> doubles as the occupancy
    ///     flag — an entry always has a realm — so an empty slot is <c>default</c> and nothing has to
    ///     be cleared to make it one.
    /// </summary>
    private struct Slot
    {
        // The IdentityBoard instance the canonical Address was derived from, so the lowercase form is
        // recomputed only when a different wallet lands on this slot.
        public string? Wallet;
        public string? Address;

        public string? Realm;
        public ParcelCoord Parcel;
    }
}
