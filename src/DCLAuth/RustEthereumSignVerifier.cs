using DCL.RustEthereum;

namespace DCL.Auth;

public class RustEthereumSignVerifier : ISignatureVerifier
{
    public bool Verify(string expectedSignerAddress, string payload, string signatureHex) =>
        RustEth.Verify(expectedSignerAddress, payload, signatureHex);
}
