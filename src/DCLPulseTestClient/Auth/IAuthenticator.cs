namespace PulseTestClient.Auth;

public record LoginResult(string AuthChainJson, string WalletAddress);

public interface IAuthenticator
{
    public Task<LoginResult> LoginAsync(string account, CancellationToken ct);
}