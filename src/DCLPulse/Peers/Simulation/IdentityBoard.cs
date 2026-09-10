using System.Collections.Concurrent;

namespace Pulse.Peers.Simulation;

/// <summary>
///     Shared store for peer wallet addresses, indexed by <see cref="PeerIndex" />.
///     Written once per peer at authentication time; read by any worker during simulation.
///     Also holds each peer's session key, written and cleared together with the wallet.
///     <para />
///     Thread safety: .NET guarantees atomic reference reads/writes. A single
///     <see cref="Volatile.Write{T}" /> at registration and <see cref="Volatile.Read{T}" />
///     at lookup is sufficient — no seqlock needed because the value never mutates after write.
///     The wallet and session slots are written by two separate volatile writes, so a reader could
///     observe the wallet before the session; this is safe because a peer is not placed in any grid
///     until its handshake has returned, so the tracker never reads a half-written pair.
/// </summary>
public sealed class IdentityBoard(int maxPeers)
{
    private readonly string?[] walletsByPeerIds = new string?[maxPeers];
    private readonly string?[] sessionsByPeerIds = new string?[maxPeers];
    private readonly ConcurrentDictionary<string, PeerIndex> peerIdsByWallets = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Registers a wallet whose auth chain carried no delegation, so the session key is the wallet.
    /// </summary>
    public void Set(PeerIndex id, string walletId) =>
        Set(id, walletId, walletId);

    /// <summary>
    ///     Registers the wallet and the session key it authenticated with: the lower-cased ephemeral
    ///     address of its auth chain. Distinct per device, stable across one device's reconnects.
    /// </summary>
    public void Set(PeerIndex id, string walletId, string session)
    {
        Volatile.Write(ref walletsByPeerIds[(int)id.Value], walletId);
        Volatile.Write(ref sessionsByPeerIds[(int)id.Value], session);
        peerIdsByWallets[walletId] = id;
    }

    public string? GetWalletIdByPeerIndex(PeerIndex id) =>
        Volatile.Read(ref walletsByPeerIds[(int)id.Value]);

    public string? GetSessionByPeerIndex(PeerIndex id) =>
        Volatile.Read(ref sessionsByPeerIds[(int)id.Value]);

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
        Volatile.Write(ref sessionsByPeerIds[(int)id.Value], null);
    }
}
