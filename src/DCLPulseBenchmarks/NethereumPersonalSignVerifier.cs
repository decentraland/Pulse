using Nethereum.Signer;

namespace DCL.Auth;

/// <summary>
/// EIP-191 personal_sign style verifier (most common).
/// </summary>
public sealed class NethereumPersonalSignVerifier : ISignatureVerifier
{
    private readonly EthereumMessageSigner signer = new();

    public bool Verify(string expectedSignerAddress, string payload, string signatureHex)
    {
        if (string.IsNullOrWhiteSpace(expectedSignerAddress)) return false;
        if (string.IsNullOrWhiteSpace(signatureHex) || !signatureHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;

        // Recover address from signature over UTF-8 message (EIP-191)
        string recovered;
        try
        {
            recovered = signer.EncodeUTF8AndEcRecover(payload, signatureHex);
        }
        catch
        {
            return false;
        }

        return NormalizeAddress(recovered) == NormalizeAddress(expectedSignerAddress);
    }

    private static string NormalizeAddress(string addr)
        => addr.Trim().ToLowerInvariant();
}
