namespace DCL.RustEthereum;

public static class Eth
{
    public static bool Verify(string expectedSignerAddress, string message, string signature)
    {
        string hex = signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? signature.Substring(2)
            : signature;

        byte[] sigBytes = HexToBytes(hex);

        unsafe
        {
            fixed (byte* ptr = sigBytes)
            {
                return Native.EthVerifyMessage(expectedSignerAddress, message, ptr);
            }
        }
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
