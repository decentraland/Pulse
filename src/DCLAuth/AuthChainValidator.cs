using System.Globalization;

namespace DCL.Auth;

public sealed class AuthChainValidator
{
    private readonly ISignatureVerifier verifier;
    private readonly HashSet<string> allowedPurposes;

    public AuthChainValidator(ISignatureVerifier verifier, IEnumerable<string>? allowedPurposes = null)
    {
        this.verifier = verifier;
        this.allowedPurposes = (allowedPurposes ?? ["Decentraland Login"])
                              .Select(p => p.Trim())
                              .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Validates SIGNER + 0..n ECDSA_EPHEMERAL + final action link.
    /// Returns the user address and the final signing authority (user or last delegate).
    /// </summary>
    public AuthChainValidationResult Validate(
        IReadOnlyList<AuthLink> chain,
        string expectedPayload,
        DateTimeOffset? now = null)
    {
        if (chain.Count < 2)
            throw new InvalidOperationException("AuthChain must contain at least SIGNER + final link.  [oai_citation:5‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)");

        now ??= DateTimeOffset.UtcNow;

        // 1) SIGNER link rules  [oai_citation:6‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)
        var first = chain[0];
        if (!string.Equals(first.Type, "SIGNER", StringComparison.Ordinal))
            throw new InvalidOperationException("First authChain link must have type SIGNER.");
        if (string.IsNullOrWhiteSpace(first.Payload) || !first.Payload.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SIGNER payload must be an ethereum address (0x...).");
        if (!string.IsNullOrEmpty(first.Signature))
            throw new InvalidOperationException("SIGNER signature must be empty.");

        string user = NormalizeAddress(first.Payload);

        // "currentAuthority" is who must sign the next step’s payload.
        string currentAuthority = user;

        // 2) Delegations: ECDSA_EPHEMERAL (0..n)
        for (int i = 1; i < chain.Count - 1; i++)
        {
            var link = chain[i];
            if (!string.Equals(link.Type, "ECDSA_EPHEMERAL", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected intermediate link type '{link.Type}'. Expected ECDSA_EPHEMERAL.  [oai_citation:7‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)");

            (string purpose, string ephemeralAddress, DateTimeOffset expiration) = ParseEphemeralPayload(link.Payload);

            if (!allowedPurposes.Contains(purpose))
                throw new InvalidOperationException($"Ephemeral purpose '{purpose}' is not allowed.");

            if (expiration <= now.Value)
                throw new InvalidOperationException("Ephemeral delegation is expired.  [oai_citation:8‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)");

            // Verify signature by previous authority over this payload  [oai_citation:9‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)
            if (!verifier.Verify(currentAuthority, link.Payload, link.Signature))
                throw new InvalidOperationException("Invalid signature for ECDSA_EPHEMERAL payload.");

            // Once validated, the next step must be signed by the ephemeral address.
            currentAuthority = NormalizeAddress(ephemeralAddress);
        }

        // 3) Final action link (type is app-specific; Decentraland defines some standard types like ECDSA_SIGNED_ENTITY)  [oai_citation:10‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)
        var final = chain[^1];

        if (final.Payload != expectedPayload)
            throw new InvalidOperationException($"Final link rejected by policy (type={final.Type}).");

        if (!verifier.Verify(currentAuthority, final.Payload, final.Signature))
            throw new InvalidOperationException("Invalid signature for final payload.");

        return new AuthChainValidationResult(
            UserAddress: user,
            CurrentAuthorityAddress: currentAuthority,
            FinalLink: final,
            Chain: chain.ToList()
        );
    }

    /// <summary>
    /// Payload format is exactly 3 lines:
    ///   <purpose>
    ///   Ephemeral address: <delegate-address>
    ///   Expiration: <date>
    ///  [oai_citation:11‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)
    /// </summary>
    private static (string Purpose, string EphemeralAddress, DateTimeOffset Expiration) ParseEphemeralPayload(string payload)
    {
        // Normalize line endings, keep empty lines if any.
        var lines = payload.Replace("\r\n", "\n").Split('\n');
        if (lines.Length != 3)
            throw new InvalidOperationException("ECDSA_EPHEMERAL payload must have exactly 3 lines.  [oai_citation:12‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)");

        var purpose = lines[0].Trim();
        var line2 = lines[1].Trim();
        var line3 = lines[2].Trim();

        const string addrPrefix = "Ephemeral address:";
        const string expPrefix = "Expiration:";

        if (!line2.StartsWith(addrPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("ECDSA_EPHEMERAL line 2 must start with 'Ephemeral address:'.");
        if (!line3.StartsWith(expPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("ECDSA_EPHEMERAL line 3 must start with 'Expiration:'.");

        var addr = line2.Substring(addrPrefix.Length).Trim();
        var exp = line3.Substring(expPrefix.Length).Trim();

        if (!addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ephemeral address must be 0x...");

        if (!DateTimeOffset.TryParse(exp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiration))
            throw new InvalidOperationException("Expiration must be ISO-8601 parseable.  [oai_citation:13‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/authchain)");

        return (purpose, addr, expiration);
    }

    private static string NormalizeAddress(string addr) => addr.Trim().ToLowerInvariant();
}
