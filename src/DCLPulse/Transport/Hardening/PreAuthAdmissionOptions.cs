namespace Pulse.Transport.Hardening;

public sealed class PreAuthAdmissionOptions
{
    public const string SECTION_NAME = "Transport:Hardening:PreAuth";

    /// <summary>
    ///     Maximum number of connections allowed to be simultaneously in PENDING_AUTH across the
    ///     entire server. The remaining <c>MaxPeers - PreAuthBudget</c> pool capacity is reserved
    ///     for already authenticated peers, so a pre-auth flood cannot lock legitimate players
    ///     out. Zero disables the limit (useful in development and for load tests).
    /// </summary>
    public int PreAuthBudget { get; set; } = 512;

    /// <summary>
    ///     Maximum concurrent PENDING_AUTH connections permitted from a single source IP.
    ///     Authenticated peers do not count — once a connection passes ECDSA validation it no
    ///     longer consumes a slot against its IP. This keeps the limiter effective against
    ///     pre-auth squatting while staying friendly to NAT / CGNAT / VPN / corporate egress
    ///     where many legitimate users share one public IP.
    ///     <para />
    ///     Steady-state legitimate throughput per IP ≈ <c>MaxConcurrentPreAuthPerIP</c> per second.
    ///     Zero disables the per-IP limit (useful in development and for load tests).
    /// </summary>
    public int MaxConcurrentPreAuthPerIP { get; set; } = 32;
}
