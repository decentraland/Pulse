namespace DCL.Auth;

public interface ISignatureVerifier
{
    /// <summary>
    /// Verifies that <paramref name="signatureHex"/> is a valid signature over <paramref name="payload"/>
    /// produced by <paramref name="expectedSignerAddress"/> (0x...).
    /// </summary>
    bool Verify(string expectedSignerAddress, string payload, string signatureHex);
}
