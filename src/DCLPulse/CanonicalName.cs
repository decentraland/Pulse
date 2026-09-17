namespace Pulse;

/// <summary>
///     Lowercasing for the two identifiers Pulse partitions on: the realm and the wallet address.
///     Both arrive from the wire in whatever casing the client chose, and both are compared
///     <see cref="StringComparison.Ordinal" /> everywhere they matter — grid keys, change detection,
///     the presence feed, the <c>/realms/{realm}</c> routes — so one casing has to win, and the
///     platform contract (C1.5) picks lowercase.
/// </summary>
public static class CanonicalName
{
    /// <summary><paramref name="value" /> in lowercase — the same instance when it already is.</summary>
    public static string Of(string value)
    {
        foreach (char c in value)
            if (char.IsUpper(c))
                return value.ToLowerInvariant();

        return value;
    }

    /// <summary><see cref="Of" />, passing null through for an optional realm.</summary>
    public static string? OrNull(string? value) =>
        value is null ? null : Of(value);
}
