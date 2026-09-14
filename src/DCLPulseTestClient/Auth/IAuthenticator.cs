namespace PulseTestClient.Auth;

public record LoginResult(string AuthChainJson, string WalletAddress);

public interface IAuthenticator
{
    /// <param name="account">MetaForge account name to sign with.</param>
    /// <param name="device">
    ///     Device label forwarded to MetaForge's <c>--device</c> flag, or null for the account's default
    ///     identity. Lets the same account hold several independent live ephemeral identities at once —
    ///     e.g. two bots simulating a takeover on one wallet.
    /// </param>
    public Task<LoginResult> LoginAsync(string account, string? device, CancellationToken ct);

    /// <summary>
    ///     Signs <paramref name="payload" /> verbatim — no lowercasing, no signed-fetch wrapping — and
    ///     returns the resulting auth chain as a JSON array, ready for
    ///     <c>SignedChallengeMessage.AuthChainJson</c>.
    /// </summary>
    /// <param name="device">See <see cref="LoginAsync" />.</param>
    public Task<string> SignPayloadAsync(string account, string? device, string payload, CancellationToken ct);
}
