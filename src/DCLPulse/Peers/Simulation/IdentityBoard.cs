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
///     Wallet, session and registration are published together as one immutable reference. A
///     tracker reading a weakly consistent grid can never combine two registrations of a slot.
/// </summary>
public sealed class IdentityBoard(int maxPeers)
{
    private readonly IdentityRegistration?[] identitiesByPeerIds = new IdentityRegistration?[maxPeers];
    private readonly ConcurrentDictionary<string, PeerIndex> peerIdsByWallets = new (StringComparer.OrdinalIgnoreCase);
    private long nextRegistration;

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
        var identity = new IdentityRegistration(walletId, session, Interlocked.Increment(ref nextRegistration));
        Volatile.Write(ref identitiesByPeerIds[(int)id.Value], identity);
        peerIdsByWallets[walletId] = id;
    }

    public string? GetWalletIdByPeerIndex(PeerIndex id) =>
        GetIdentity(id)?.Wallet;

    /// <summary>
    ///     The slot's wallet, session and registration as one immutable reference, or null for an
    ///     unregistered slot. The registration changes on every <see cref="Set" />, including a
    ///     same-wallet, same-session reconnect, so a slow reader can detect slot reuse even when it
    ///     never observes an empty slot.
    /// </summary>
    public IdentityRegistration? GetIdentity(PeerIndex id) =>
        Volatile.Read(ref identitiesByPeerIds[(int)id.Value]);

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

        Volatile.Write(ref identitiesByPeerIds[(int)id.Value], null);
    }
}

public sealed record IdentityRegistration(string Wallet, string Session, long Registration);
