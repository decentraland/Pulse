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
    private readonly string?[] walletIds = new string?[maxPeers];

    public void Set(PeerIndex id, string walletId)
    {
        Volatile.Write(ref walletIds[(int)id.Value], walletId);
    }

    public string? Get(PeerIndex id) =>
        Volatile.Read(ref walletIds[(int)id.Value]);

    public void Clear(PeerIndex id)
    {
        Volatile.Write(ref walletIds[(int)id.Value], null);
    }
}
