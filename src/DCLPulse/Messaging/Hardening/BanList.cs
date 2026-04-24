namespace Pulse.Messaging.Hardening;

/// <summary>
///     Shared wallet blocklist. <see cref="BanEnforcer" /> atomically swaps the underlying set
///     on each ban-list refresh; readers — <see cref="HandshakeHandler" /> on the worker
///     thread, the enforcer itself during the eviction scan — see a consistent snapshot
///     without locking. Case-insensitive because Gatekeeper may not return checksum-matching
///     wallet addresses.
/// </summary>
public sealed class BanList
{
    private HashSet<string> banned = new (StringComparer.OrdinalIgnoreCase);

    // Reused across calls — Replace is invoked serially by the single polling thread, and
    // the returned view is consumed synchronously by that same thread before the next call.
    // Callers must therefore treat the returned collection as valid only until the next
    // Replace; see the Replace doc for the caller contract.
    private readonly List<string> newlyBannedBuffer = new ();

    public bool IsBanned(string walletAddress) =>
        Volatile.Read(ref banned).Contains(walletAddress);

    /// <summary>
    ///     Replaces the blocklist with <paramref name="addresses" />. Returns the addresses
    ///     that were not in the previous set — the enforcer uses this to find peers whose
    ///     wallet just became banned so they can be kicked mid-session.
    ///     <para />
    ///     The returned collection is a reusable internal buffer: it is valid until the next
    ///     <see cref="Replace" /> call, at which point its contents are overwritten. Callers
    ///     must finish iterating before calling <see cref="Replace" /> again.
    /// </summary>
    public IReadOnlyCollection<string> Replace(IEnumerable<string> addresses)
    {
        var next = new HashSet<string>(addresses, StringComparer.OrdinalIgnoreCase);
        HashSet<string> previous = Interlocked.Exchange(ref banned, next);

        newlyBannedBuffer.Clear();

        foreach (string wallet in next)
            if (!previous.Contains(wallet))
                newlyBannedBuffer.Add(wallet);

        return newlyBannedBuffer;
    }
}
