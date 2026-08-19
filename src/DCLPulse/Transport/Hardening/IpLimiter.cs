using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;
using System.Net;

namespace Pulse.Transport.Hardening;

/// <summary>
///     Hard cap on concurrent connections per source IP. Sibling of
///     <see cref="PreAuthAdmission" />, deliberately not merged with it: this counts <em>every</em>
///     connection including authenticated ones, releases only on disconnect, and is enforced
///     <em>before</em> a <see cref="PeerIndex" /> is allocated — a flooding IP never touches the
///     allocator's pending-recycle state, never creates a peer, never reaches a worker.
///     <see cref="PreAuthAdmission" /> counts only PENDING_AUTH peers and frees them on promotion.
///     <para />
///     <b>Always count, gate only enforcement.</b> With <see cref="IpLimiterOptions.Enabled" />
///     false or <see cref="IpLimiterOptions.MaxConcurrency" /> zero, <see cref="TryAcquire" />
///     still increments and <see cref="Release" /> / <see cref="Abandon" /> still decrement; only
///     the refusal branch is skipped. The options are runtime-reconfigurable, so counting that
///     paused while disabled would let re-enabling resume from a zero baseline and over-admit
///     until the whole population churned. Whitelisted IPs are counted for the same reason. Do not
///     "optimise" the bookkeeping away — connect rate is low.
///     <para />
///     Every string that reaches a dictionary passes <see cref="Normalize" /> first, whitelist
///     entries included, so the two transports' spellings of one address share a single budget.
///     <para />
///     Threading: <see cref="TryAcquire" />, <see cref="Bind" /> and <see cref="Abandon" /> run on
///     the ENet and WebTransport threads, <see cref="Release" /> on the owning worker from the peer
///     Disconnected event — the lock is load-bearing, not decorative. One lock guards both
///     dictionaries so the per-IP counts and the peer-to-IP index cannot drift apart. Metrics are
///     recorded outside it, because <c>MeterListener</c> callbacks run synchronously on the
///     recording thread and a slow listener would extend a contended critical section. The
///     whitelist is read lock-free through an immutable snapshot swapped on configuration change,
///     the same pattern as <c>BanList</c>.
/// </summary>
public sealed class IpLimiter : IDisposable
{
    private readonly Lock syncRoot = new ();

    // Keys are canonical (see Normalize); the comparer only covers what canonicalisation cannot —
    // input IPAddress refuses to parse, kept verbatim. Entries are removed at zero, never left at
    // zero: a stale 0 entry would decrement to -1 on a surplus release and widen the cap.
    private readonly Dictionary<string, int> perIpCounts = new (StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PeerIndex, string> ipByPeer = new ();
    private readonly IOptionsMonitor<IpLimiterOptions> options;
    private readonly ILogger<IpLimiter> logger;
    private readonly IDisposable? whitelistSubscription;

    // Immutable snapshot replaced wholesale on configuration change, so readers never re-split the
    // raw string on the connect path. Volatile: written from the options-monitor callback thread,
    // read from both transport threads.
    private HashSet<string> whitelist;

    /// <summary>Number of distinct source IPs currently holding at least one connection.</summary>
    public int TrackedIps
    {
        get { lock (syncRoot) return perIpCounts.Count; }
    }

    public IpLimiter(IOptionsMonitor<IpLimiterOptions> options, ILogger<IpLimiter> logger)
    {
        this.options = options;
        this.logger = logger;
        whitelist = ParseWhitelist(options.CurrentValue.Whitelist);

        if (whitelist.Count > 0)
            logger.LogInformation("IP limiter whitelist loaded: {Count} entries [{Entries}].",
                whitelist.Count, string.Join(", ", whitelist));

        whitelistSubscription = options.OnChange(OnOptionsChanged);
    }

    /// <summary>
    ///     Swaps in the reparsed exemption list and reports it when it actually differs.
    ///     <c>OnChange</c> fires for every knob in the section and once per configuration provider,
    ///     so the sets are compared rather than logged per callback. <see cref="Interlocked.Exchange{T}" />
    ///     rather than <see cref="Volatile.Write{T}" />: the previous set is needed for the diff, and
    ///     reading it separately would let two concurrent reloads report the same transition twice.
    /// </summary>
    private void OnOptionsChanged(IpLimiterOptions changed)
    {
        HashSet<string> next = ParseWhitelist(changed.Whitelist);
        HashSet<string> previous = Interlocked.Exchange(ref whitelist, next);

        if (previous.SetEquals(next))
            return;

        LogWhitelistChange(previous, next);
    }

    /// <summary>
    ///     Reports the entries added and removed plus the resulting set, in the canonical form the
    ///     limiter matches against — an operator entering a v4-mapped spelling sees the v4 form that
    ///     actually takes effect. The exemption list is the one knob here that silently widens the
    ///     cap, so every transition is on the record.
    /// </summary>
    private void LogWhitelistChange(HashSet<string> previous, HashSet<string> next)
    {
        var added = new List<string>();
        var removed = new List<string>();

        foreach (string entry in next)
            if (!previous.Contains(entry))
                added.Add(entry);

        foreach (string entry in previous)
            if (!next.Contains(entry))
                removed.Add(entry);

        logger.LogInformation(
            "IP limiter whitelist changed — added [{Added}], removed [{Removed}]; now {Count} entries [{Entries}].",
            string.Join(", ", added), string.Join(", ", removed), next.Count, string.Join(", ", next));
    }

    public void Dispose() =>
        whitelistSubscription?.Dispose();

    /// <summary>
    ///     Reserves one connection slot for <paramref name="ip" />. Called on a transport thread at
    ///     the top of the Connect event, before the PeerIndex allocation. <c>false</c> means the IP
    ///     is at its cap and not whitelisted — nothing was reserved, so there is no rollback to do.
    ///     On <c>true</c> the reservation is held against the IP until <see cref="Bind" /> ties it
    ///     to a peer (freed later by <see cref="Release" />) or <see cref="Abandon" /> rolls it back.
    ///     <para />
    ///     Options are read per call rather than captured at construction: these knobs are
    ///     runtime-reconfigurable and the connect path is not a hot path.
    /// </summary>
    public bool TryAcquire(string ip)
    {
        IpLimiterOptions current = options.CurrentValue;
        int cap = current.MaxConcurrency;
        bool enforcing = current.Enabled && cap > 0;
        string key = Normalize(ip);

        if (key.Length == 0)
            return TryAcquireUnidentified(enforcing);

        bool refused;
        bool bypassedByWhitelist;
        var firstForThisIp = false;

        lock (syncRoot)
        {
            perIpCounts.TryGetValue(key, out int count);

            bool atCap = enforcing && count >= cap;
            bypassedByWhitelist = atCap && Volatile.Read(ref whitelist).Contains(key);
            refused = atCap && !bypassedByWhitelist;

            if (!refused)
            {
                perIpCounts[key] = count + 1;
                firstForThisIp = count == 0;
            }
        }

        if (refused)
        {
            PulseMetrics.Hardening.IP_LIMIT_REFUSED.Add(1);
            return false;
        }

        if (bypassedByWhitelist)
            PulseMetrics.Hardening.IP_LIMIT_WHITELIST_BYPASS.Add(1);

        if (firstForThisIp)
            PulseMetrics.Hardening.IP_LIMIT_TRACKED_IPS.Add(1);

        return true;
    }

    /// <summary>
    ///     Admission decision for a peer whose source address the transport could not render —
    ///     ENet's <c>Peer.IP</c> yields the empty string when <c>enet_peer_get_ip</c> fails. Refused
    ///     while enforcing: an unattributable peer cannot be limited, and pooling all such peers
    ///     into one shared bucket would let any one of them lock out the rest while telling an
    ///     operator nothing about who was connecting. Refusing preserves the invariant that an
    ///     admitted connection is always attributable. While disabled or capped at zero the peer is
    ///     admitted and left uncounted: there is no identity to count it against, and a disabled
    ///     limiter must never refuse.
    /// </summary>
    private static bool TryAcquireUnidentified(bool enforcing)
    {
        if (!enforcing)
            return true;

        PulseMetrics.Hardening.IP_LIMIT_REFUSED.Add(1);
        return false;
    }

    /// <summary>
    ///     Commits the reservation taken by <see cref="TryAcquire" /> to
    ///     <paramref name="peerIndex" />, so <see cref="Release" /> can free it from the worker.
    ///     Called on a transport thread once every admission check has passed.
    /// </summary>
    public void Bind(PeerIndex peerIndex, string ip)
    {
        string key = Normalize(ip);

        lock (syncRoot) ipByPeer[peerIndex] = key;
    }

    /// <summary>
    ///     Rolls back a reservation that never became a peer — allocator exhaustion or a
    ///     <see cref="PreAuthAdmission" /> refusal, neither of which ever produces the Disconnected
    ///     lifecycle event that drives <see cref="Release" />. Called on a transport thread, at most
    ///     once per successful <see cref="TryAcquire" />: keyed by IP alone, so unlike
    ///     <see cref="Release" /> it cannot be idempotent. A call for an IP holding no reservations
    ///     is a no-op.
    /// </summary>
    public void Abandon(string ip)
    {
        string key = Normalize(ip);
        bool entryRemoved;

        lock (syncRoot) entryRemoved = DecrementLocked(key);

        if (entryRemoved)
            PulseMetrics.Hardening.IP_LIMIT_TRACKED_IPS.Add(-1);
    }

    /// <summary>
    ///     Frees the connection slot bound to <paramref name="peerIndex" />. Called on the
    ///     owning worker thread from the peer Disconnected lifecycle event. Idempotent through
    ///     lookup-and-clear: a duplicate call finds no binding and decrements nothing.
    /// </summary>
    public void Release(PeerIndex peerIndex)
    {
        bool entryRemoved;

        lock (syncRoot)
        {
            if (!ipByPeer.Remove(peerIndex, out string? ip)) return;

            entryRemoved = DecrementLocked(ip);
        }

        if (entryRemoved)
            PulseMetrics.Hardening.IP_LIMIT_TRACKED_IPS.Add(-1);
    }

    /// <summary>
    ///     Canonical dictionary key for a source address. ENet's <c>Peer.IP</c> is dotted IPv4,
    ///     v4-mapped IPv6 (<c>::ffff:a.b.c.d</c>) or native IPv6; WebTransport reports whichever
    ///     family the native host parsed. Without one canonical form a host holds
    ///     <see cref="IpLimiterOptions.MaxConcurrency" /> under each spelling — double the cap from
    ///     a single address — and a dotted whitelist entry misses a v4-mapped peer. Mapping
    ///     v4-mapped down to IPv4 and round-tripping the rest through <see cref="IPAddress" />
    ///     collapses those spellings plus IPv6 hex casing and zero-run compression, the same
    ///     reduction <c>ContinentResolver</c> applies before a geo lookup.
    ///     <para />
    ///     Input <see cref="IPAddress" /> cannot parse is returned verbatim, which keeps the
    ///     dictionary comparer's case-insensitivity meaningful. ENet's empty string on
    ///     <c>enet_peer_get_ip</c> failure therefore stays empty, and
    ///     <see cref="TryAcquireUnidentified" /> picks it off before it can reach a dictionary.
    /// </summary>
    private static string Normalize(string ip) =>
        IPAddress.TryParse(ip, out IPAddress? address)
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
            : ip;

    private static HashSet<string> ParseWhitelist(string raw)
    {
        var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(raw)) return parsed;

        foreach (string entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            parsed.Add(Normalize(entry));

        return parsed;
    }

    /// <summary>
    ///     Drops one reservation for <paramref name="ip" />, removing the entry entirely at zero so
    ///     the table stays bounded by concurrent connections rather than by distinct IPs ever seen.
    ///     Caller must hold <see cref="syncRoot" /> and pass an already-normalised key. Returns
    ///     <c>true</c> when the entry was removed, so the tracked-IP metric can be recorded outside
    ///     the lock.
    /// </summary>
    private bool DecrementLocked(string ip)
    {
        if (!perIpCounts.TryGetValue(ip, out int count)) return false;

        if (count > 1)
        {
            perIpCounts[ip] = count - 1;
            return false;
        }

        perIpCounts.Remove(ip);
        return true;
    }
}
