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
///             a peer leaving, published by <see cref="OnPeerRemoved" /> alone (C1.2).
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

            if (snapshot)
                feed.PublishParcelSnapshot(CollectLivePresence(), reason);
        }
    }

    /// <summary>
    ///     The single exit choke point: one <c>parcel</c>-absent entry for a peer that had a published
    ///     presence, and nothing at all for one that never did — a peer that timed out in
    ///     <c>PENDING_AUTH</c> was never on the feed, so it has no departure to announce.
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

            if (slot.Realm is { } realm)
            {
                liveCount--;
                feed.PublishParcelChange(slot.Address!, realm, parcel: null);
            }

            slot = default(Slot);
        }
    }

    /// <summary>
    ///     Brings one peer's slot up to date and, when <paramref name="publishChange" /> is set,
    ///     publishes the entry if its <c>(realm, parcel)</c> moved. A peer with no slot yet has
    ///     changed by definition: its previous state is "nowhere".
    /// </summary>
    private void Observe(ClusterPeerInfo info, bool publishChange)
    {
        ref Slot slot = ref slots[(int)info.Peer.Value];

        parcelEncoder.Decode(info.Parcel, out int x, out int z);

        var parcel = new ParcelCoord(x, z);
        string realm = info.Realm;

        if (slot.Realm is not null
            && slot.Parcel == parcel
            && string.Equals(slot.Realm, realm, StringComparison.Ordinal))
            return;

        if (slot.Realm is null)
            liveCount++;

        // The wallet string is one instance per peer slot for as long as the peer lives, so the
        // lowercase form is computed once per peer rather than once per published entry.
        if (!ReferenceEquals(slot.Wallet, info.Wallet))
        {
            slot.Wallet = info.Wallet;
            slot.Address = CanonicalName.Of(info.Wallet);
        }

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
