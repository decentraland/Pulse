using Pulse.Peers;

namespace Pulse.Clusters;

/// <summary>
///     The <c>engine.parcel_changes</c> half of the tracker (iteration-2 C1): the presence columns of
///     <see cref="PeerState" />, brought up to date by the pass and retired through the worker handoff.
/// </summary>
public sealed partial class ClusterTracker
{
    // Whether engine.parcel_changes is derived at all. No broker means nothing to publish to.
    private readonly bool presenceEnabled;

    // Whether a pass will ever run to drain departures. Without it a disabled tracker would let the
    // queue grow for the life of the process.
    private readonly bool passRuns;

    // Departure handoff: a worker raises a flag, the next pass retires the peer. The only state of
    // this class another thread writes, which is what leaves peerStates single-threaded.
    private readonly bool[] departed;

    // Peers holding a published presence, so a snapshot's list is sized once instead of grown.
    private int liveCount;

    // Order presence occupancies were acquired in, which breaks a tie between two slots of one
    // wallet. ulong at one pass per second, so it cannot wrap.
    private ulong occupancyStamp;

    /// <summary>
    ///     Whether <c>engine.parcel_changes</c> is derived. False leaves every presence entry point a
    ///     no-op; clustering runs regardless.
    /// </summary>
    public bool PresenceEnabled => presenceEnabled;

    /// <summary>
    ///     The single presence exit choke point (C1.2): one <c>parcel</c>-absent entry for a peer that
    ///     had a published presence, nothing for one that never did. Called from a peer worker.
    ///     <para />
    ///     Wallet-scoped (A1) — the entry goes out only once no live peer is bound to the wallet. A
    ///     duplicate-session kick rebinds the wallet to the incoming peer long before this runs, so
    ///     publishing here would take a peer offline that is online on its newer connection; and with
    ///     both in one batch the per-address coalescing keeps the exit and drops the placement.
    ///     <para />
    ///     Clearing the columns is mandatory, not tidy: <see cref="PeerIndex" /> is recycled, and
    ///     leftover state would make the next wallet's first placement look unchanged.
    /// </summary>
    public void OnPeerRemoved(PeerIndex peer)
    {
        if (!presenceEnabled || !passRuns) return;

        var index = (int)peer.Value;

        if (index >= departed.Length) return;

        // Raised before PeerSimulation releases the index, so the drain still finds this session's
        // columns: a reissued index is only placed later in a pass than the drain runs.
        Volatile.Write(ref departed[index], true);
    }

    /// <summary>
    ///     Retires every peer a worker handed over since the last pass. Runs first, so an exit
    ///     precedes this pass's placements and cannot be contradicted by its snapshot.
    /// </summary>
    private void DrainDepartures()
    {
        if (!presenceEnabled) return;

        for (var index = 0; index < departed.Length; index++)
        {
            if (!Volatile.Read(ref departed[index])) continue;

            Volatile.Write(ref departed[index], false);
            RetirePresence(new PeerIndex((uint)index));
        }
    }

    /// <summary>
    ///     One departed peer: clears its presence columns and publishes the exit, unless the wallet is
    ///     left placed elsewhere on this server.
    /// </summary>
    private void RetirePresence(PeerIndex peer)
    {
        ref PeerState state = ref peerStates[(int)peer.Value];

        if (state.Realm is not { } realm)
        {
            ClearPresence(ref state);

            return;
        }

        string address = state.Address!;
        string wallet = state.Wallet!;

        // Cleared before the binding is read, so this slot cannot answer for itself.
        ClearPresence(ref state);
        liveCount--;

        // Suppressed only when the replacement is placed, not merely bound (A1): the legacy connect
        // flow binds at AUTHENTICATED and places on the first teleport, so a binding alone would
        // swallow the exit of a reconnect that drops in between.
        if (identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex live)
            && live != peer
            && live.Value < peerStates.Length
            && peerStates[live.Value].Realm is not null) return;

        feedPublisher.PublishParcelChange(address, realm, parcel: null);
    }

    /// <summary>
    ///     Clears the presence columns alone. The clustering columns belong to the pass thread and are
    ///     retired by <see cref="ForgetVanishedPeers" />; writing the whole struct here would race it.
    /// </summary>
    private static void ClearPresence(ref PeerState state)
    {
        state.Wallet = null;
        state.Address = null;
        state.Realm = null;
        state.Parcel = default(ParcelCoord);
        state.OccupiedAt = 0;
    }

    /// <summary>
    ///     Brings one member's presence columns up to date and, when <paramref name="publishChange" />
    ///     is set, publishes an entry if its <c>(realm, parcel)</c> moved. A member with no presence
    ///     yet has changed by definition: its previous state is "nowhere" (C1.1).
    /// </summary>
    private void ObservePresence(PassMember member, string realm, bool publishChange)
    {
        ref PeerState state = ref peerStates[member.Peer.Value];

        parcelEncoder.Decode(member.Parcel, out int x, out int z);

        var parcel = new ParcelCoord(x, z);

        // A different wallet on this slot is a new presence wherever it stands — guarding the same
        // aliasing class as PeerSimulation.DetectAndHandleAliasing. Reference equality is the fast
        // path: IdentityBoard hands out one string instance per peer for as long as it lives.
        bool sameWallet = ReferenceEquals(state.Wallet, member.Wallet)
                       || string.Equals(state.Wallet, member.Wallet, StringComparison.OrdinalIgnoreCase);

        if (state.Realm is not null
            && sameWallet
            && state.Parcel == parcel
            && string.Equals(state.Realm, realm, StringComparison.Ordinal))
            return;

        // Read before state.Realm is written below, since that is what marks an empty slot.
        bool acquired = state.Realm is null || !sameWallet;

        if (state.Realm is null)
            liveCount++;

        state.Wallet = member.Wallet;

        // Lowercased once per wallet rather than once per published entry.
        if (!sameWallet)
            state.Address = CanonicalName.Of(member.Wallet);

        state.Realm = realm;
        state.Parcel = parcel;

        // On acquisition only, never on a later move, so the tie-break is recency of the *session*.
        // A kicked connection keeps moving for a client round trip, so a stamp rewritten on every
        // change would be the stale slot's whenever it moved last — and which moved last is decided
        // by grid.GetOccupiedCells() order, which is spatial. Occupancy order is monotone in session
        // age; placement order is not.
        if (acquired)
            state.OccupiedAt = ++occupancyStamp;

        if (publishChange)
            feedPublisher.PublishParcelChange(state.Address!, realm, parcel);
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

        for (var index = 0; index < peerStates.Length; index++)
        {
            ref PeerState state = ref peerStates[index];

            if (state.Realm is not { } realm) continue;

            string address = state.Address!;
            var entry = new PeerPresence(address, realm, state.Parcel);

            if (!entryByAddress.TryGetValue(address, out (int Entry, int Slot) held))
            {
                entryByAddress[address] = (presence.Count, index);
                presence.Add(entry);

                continue;
            }

            if (!Supersedes(in state, in peerStates[held.Slot])) continue;

            // Replaced in place; the publisher sorts by address anyway.
            presence[held.Entry] = entry;
            entryByAddress[address] = (held.Entry, index);
        }

        return presence;
    }

    /// <summary>
    ///     Which of two slots holding one wallet is that wallet's presence. The slot the latest pass
    ///     saw wins: <see cref="TryCollectMember" /> collects only the wallet's live binding, so a
    ///     kicked connection stops being observed once the handshake rebinds the wallet.
    ///     <para />
    ///     Both seen in one pass means a single traversal straddled that rebind — the weakly-consistent
    ///     grid read reaching the kicked connection's cell before it and the incoming one's cell after.
    ///     There the newer <b>occupancy</b> wins. Occupancy, not placement: the kicked connection is
    ///     still on the wire and is often the one that moved most recently, so placement recency would
    ///     name the parcel the player just left.
    /// </summary>
    private static bool Supersedes(in PeerState candidate, in PeerState held) =>
        candidate.LastSeenPass != held.LastSeenPass
            ? candidate.LastSeenPass > held.LastSeenPass
            : candidate.OccupiedAt > held.OccupiedAt;
}
