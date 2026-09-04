namespace Pulse;

/// <summary>
///     Lowercasing for the two identifiers Pulse partitions on: the realm and the wallet address.
///     <para />
///     Both arrive from the wire in whatever casing the client chose — a realm typed by a user, a
///     wallet in EIP-55 checksum form — and both are compared <see cref="StringComparison.Ordinal" />
///     everywhere they matter: <c>RealmSpatialGrids</c>' grid keys, <c>ClusterTracker</c>'s
///     change detection, the presence feed's per-address coalescing, the <c>/realms/{realm}</c>
///     routes. One casing has to win, and the platform contract (C1.5) picks lowercase, so both are
///     canonicalized the moment they enter.
///     <para />
///     The scan before the allocation is the point: virtually every value is already lowercase, and
///     this runs once per placement and once per peer per clustering pass.
/// </summary>
public static class CanonicalName
{
    /// <summary>
    ///     <paramref name="value" /> in lowercase — the same instance when it already is.
    /// </summary>
    public static string Of(string value)
    {
        foreach (char c in value)
            if (char.IsUpper(c))
                return value.ToLowerInvariant();

        return value;
    }

    /// <summary>
    ///     <see cref="Of" /> that passes null through, for the optional realm on a snapshot.
    /// </summary>
    public static string? OrNull(string? value) =>
        value is null ? null : Of(value);
}
