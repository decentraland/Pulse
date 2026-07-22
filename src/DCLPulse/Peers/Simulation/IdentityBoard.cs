using System.Collections.Concurrent;

namespace Pulse.Peers.Simulation;

/// <summary>
///     Shared store for peer wallet addresses, indexed by <see cref="PeerIndex" />.
///     Written once per peer at authentication time; read by any worker during simulation.
///     <para />
///     Thread safety: .NET guarantees atomic reference reads/writes. A single
///     <see cref="Volatile.Write{T}" /> at registration and <see cref="Volatile.Read{T}" />
///     at lookup is sufficient — no seqlock needed because the value never mutates after write.
/// </summary>
public sealed class IdentityBoard(int maxPeers)
{
    private readonly string?[] walletsByPeerIds = new string?[maxPeers];
    private readonly ConcurrentDictionary<string, PeerIndex> peerIdsByWallets = new (StringComparer.OrdinalIgnoreCase);

    public void Set(PeerIndex id, string walletId)
    {
        Volatile.Write(ref walletsByPeerIds[(int)id.Value], walletId);
        peerIdsByWallets[walletId] = id;
    }

    public string? GetWalletIdByPeerIndex(PeerIndex id) =>
        Volatile.Read(ref walletsByPeerIds[(int)id.Value]);

    public bool TryGetPeerIndexByWallet(string walletId, out PeerIndex peerIndex) =>
        peerIdsByWallets.TryGetValue(walletId, out peerIndex);

    public void Remove(PeerIndex id)
    {
        string? walletId = GetWalletIdByPeerIndex(id);

        // Value-checked removal: after a duplicate-session eviction the wallet is already
        // rebound to the replacement peer, and the evicted peer's delayed cleanup must not
        // delete that live mapping.
        if (walletId != null)
            peerIdsByWallets.TryRemove(new KeyValuePair<string, PeerIndex>(walletId, id));

        Volatile.Write(ref walletsByPeerIds[(int)id.Value], null);
    }
}
