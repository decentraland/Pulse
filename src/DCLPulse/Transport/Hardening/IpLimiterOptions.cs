namespace Pulse.Transport.Hardening;

/// <summary>
///     Configuration for <see cref="IpLimiter" />. Bound through <c>IOptionsMonitor</c>, never
///     <c>IOptions</c>: every knob ships as a default in <c>dynamicconfig.json</c> and can be
///     overridden live by the remote feature-flag document.
/// </summary>
public sealed class IpLimiterOptions
{
    /// <summary>
    ///     Configuration path these knobs bind from, following the house
    ///     <c>&lt;Layer&gt;:Hardening:&lt;Name&gt;</c> convention. The remote <c>pulse.json</c>
    ///     fragment and <c>dynamicconfig.json</c> nest under the same path.
    /// </summary>
    public const string SECTION_NAME = "Transport:Hardening:IpLimiter";

    /// <summary>
    ///     Master switch for enforcement. When <c>false</c> nothing is refused but connections are
    ///     still counted, so flipping back to <c>true</c> takes effect against the real concurrent
    ///     population instead of a zero baseline.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Concurrent connections accepted from one source IP, counted across both transports and
    ///     including authenticated peers. Zero disables the cap while leaving counting intact,
    ///     exactly like <see cref="Enabled" /> set to <c>false</c>.
    ///     <para />
    ///     Lowering this never evicts established connections — new ones from that IP are refused
    ///     until it drains below the new cap.
    /// </summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>
    ///     Comma-separated exact source IPs exempt from the cap, e.g. <c>"10.0.0.1,10.0.0.2"</c>;
    ///     entries are trimmed and empty ones ignored. Each is canonicalised the way an incoming
    ///     peer address is, so a dotted entry matches a peer reported as v4-mapped IPv6
    ///     (<c>::ffff:10.0.0.1</c>) and IPv6 hex casing does not matter. Exact match only — no CIDR,
    ///     so a VPN egress range must be listed address by address.
    ///     <para />
    ///     Whitelisted IPs are still counted; only the refusal is skipped. Removing an entry then
    ///     takes effect immediately against an accurate concurrency count.
    /// </summary>
    public string Whitelist { get; set; } = "";
}
