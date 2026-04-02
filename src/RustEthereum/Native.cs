using System.Runtime.InteropServices;

namespace DCL.RustEthereum;

public static class Native
{
    private const string LIBRARY_NAME = "rust-eth";

    /// <param name="expectedSignerAddress">The eth address of the signer</param>
    /// <param name="message">The original message that was signed</param>
    /// <param name="signature">The 65-byte signature to verify</param>
    /// <returns>True if the recovered signer matches the expected address</returns>
    [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "eth_verify_message")]
    internal static extern unsafe bool EthVerifyMessage(string expectedSignerAddress, string message, byte* signature);
}
