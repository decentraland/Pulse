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
    ///     Concurrent <see cref="ConnectionClass.PLAYER" /> connections accepted from one source IP,
    ///     counted across both transports and including authenticated peers — every connection is
    ///     charged here until it announces itself something else. Zero disables the cap while
    ///     leaving counting intact, exactly like <see cref="Enabled" /> set to <c>false</c>.
    ///     <para />
    ///     Lowering this never evicts established connections — new ones from that IP are refused
    ///     until it drains below the new cap.
    /// </summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>
    ///     Concurrent scene-listener connections accepted from one source IP — the same accounting
    ///     as <see cref="MaxConcurrency" /> over a separate budget, so a listener fleet running from
    ///     a handful of egress IPs does not have to be paid for by widening the player cap. Zero
    ///     disables the cap while leaving counting intact.
    ///     <para />
    ///     A connection is only charged here once its <c>SCENE_LISTENER_HANDSHAKE</c> validates; up
    ///     to that point it sits in the player budget. This is one value applied to every source IP,
    ///     not an allowance handed to a known fleet, and no allowlist stands behind it — so a fleet
    ///     that needs many connections is whitelisted by egress IP rather than paid for by raising
    ///     this. Sizing arithmetic in docs/hardening.md.
    /// </summary>
    public int SceneListenerMaxConcurrency { get; set; } = 2;

    /// <summary>
    ///     Comma-separated exact source IPs exempt from every class's cap, e.g. <c>"10.0.0.1,10.0.0.2"</c>;
    ///     entries are trimmed and empty ones ignored. Each is canonicalised the way an incoming
    ///     peer address is, so a dotted entry matches a peer reported as v4-mapped IPv6
    ///     (<c>::ffff:10.0.0.1</c>) and IPv6 hex casing does not matter. Exact match only — no CIDR,
    ///     so a VPN egress range must be listed address by address.
    ///     <para />
    ///     Whitelisted IPs are still counted; only the refusal is skipped. Removing an entry then
    ///     takes effect immediately against an accurate concurrency count.
    /// </summary>
    public string Whitelist { get; set; } = "";

    /// <summary>
    ///     Maps one <see cref="ConnectionClass" /> onto the cap key configured for it, in
    ///     concurrent connections from a single source IP. Zero means unlimited, per the house
    ///     convention.
    ///     <para />
    ///     Every class is listed explicitly and there is no discard arm on purpose: a class added
    ///     without a cap key of its own then makes the compiler flag this switch as non-exhaustive,
    ///     and reaches a <c>SwitchExpressionException</c> if it is shipped anyway. A
    ///     fallback arm would instead hand the new class the player cap silently, with no metric or
    ///     log to say so, and an operator raising <see cref="MaxConcurrency" /> would move a budget
    ///     they were not aiming at.
    /// </summary>
    public int MaxConcurrencyFor(ConnectionClass connectionClass) =>
        // CS8524 fires because a cast can produce an enum value with no name, which is true of any
        // enum and cannot be fixed without the discard arm this switch deliberately omits.
        // Suppressed here only: CS8509 — a newly added *named* class going unhandled — is a
        // different diagnostic and stays on, which is the signal the missing discard buys.
#pragma warning disable CS8524
        connectionClass switch
        {
            ConnectionClass.PLAYER => MaxConcurrency,
            ConnectionClass.SCENE_LISTENER => SceneListenerMaxConcurrency,
        };
#pragma warning restore CS8524
}
