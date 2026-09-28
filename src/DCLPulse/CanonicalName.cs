namespace Pulse;

/// <summary>
///     Lowercasing for the two identifiers Pulse partitions on: the realm and the wallet address.
///     Both are compared <see cref="StringComparison.Ordinal" /> everywhere they matter — grid keys,
///     change detection, the presence feed, the <c>/realms/{realm}</c> routes — and both go on the
///     wire in that form, so one casing has to win and the platform contract (C1.5) picks lowercase.
/// </summary>
public static class CanonicalName
{
    /// <summary>
    ///     <paramref name="value" /> in lowercase — the same instance when it already is, which is the
    ///     steady state: wallets are lowercased at the auth boundary, so only realms off the wire
    ///     allocate here.
    /// </summary>
    public static string Of(string value) =>
        value.ToLowerInvariant();
}
