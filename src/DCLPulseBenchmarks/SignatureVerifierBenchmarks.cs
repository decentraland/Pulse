using BenchmarkDotNet.Attributes;
using DCL.Auth;

namespace DCLPulseBenchmarks;

[MemoryDiagnoser]
public class SignatureVerifierBenchmarks
{
    private const string SIGNER_ADDRESS = "ADD_SIGNER_ADDRESS";
    private const string PAYLOAD = "ADD_PAYLOAD";
    private const string SIGNATURE = "ADD_SIGNATURE";

    private NethereumPersonalSignVerifier nethereum = null!;
    private RustEthereumSignVerifier rustEth = null!;

    [GlobalSetup]
    public void Setup()
    {
        nethereum = new NethereumPersonalSignVerifier();
        rustEth = new RustEthereumSignVerifier();

        // Sanity check — both must return true with real data
        if (!nethereum.Verify(SIGNER_ADDRESS, PAYLOAD, SIGNATURE))
            throw new InvalidOperationException("NethereumPersonalSignVerifier.Verify returned false — check test data");
        if (!rustEth.Verify(SIGNER_ADDRESS, PAYLOAD, SIGNATURE))
            throw new InvalidOperationException("RustEthereum.Verify returned false — check test data");
    }

    [Benchmark(Baseline = true)]
    public bool Nethereum() => nethereum.Verify(SIGNER_ADDRESS, PAYLOAD, SIGNATURE);

    [Benchmark]
    public bool RustEth() => rustEth.Verify(SIGNER_ADDRESS, PAYLOAD, SIGNATURE);
}
