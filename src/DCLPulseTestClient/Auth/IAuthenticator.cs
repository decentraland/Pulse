namespace PulseTestClient.Auth;

public record LoginResult(string AuthChainJson, string WalletAddress);

public interface IAuthenticator
{
    public Task<LoginResult> LoginAsync(string account, CancellationToken ct);

    /// <summary>
    ///     Signs <paramref name="payload" /> verbatim — no lowercasing, no signed-fetch wrapping — and
    ///     returns the resulting auth chain as a JSON array, ready for
    ///     <c>SignedChallengeMessage.AuthChainJson</c>.
    /// </summary>
    public Task<string> SignPayloadAsync(string account, string payload, CancellationToken ct);
}
