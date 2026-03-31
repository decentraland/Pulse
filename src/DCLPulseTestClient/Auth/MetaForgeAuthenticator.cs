using System.Text.Json;
using System.Text.Json.Nodes;

namespace PulseTestClient.Auth;

public class MetaForgeAuthenticator : IAuthenticator
{
    public async Task<LoginResult> LoginAsync(string account, CancellationToken ct)
    {
        await MetaForge.RunCommandAsync($"account create {account} --skip-update-check --skip-auto-login", ct);

        var output = await MetaForge.RunCommandAsync(
            $"account chain {account} --method connect --path / --metadata {{}} --skip-update-check", ct);

        AuthLink[] chain = JsonSerializer.Deserialize(output, AuthenticatorJsonContext.Default.AuthLinkArray)!;

        string walletAddress = chain.First(l => l.type == AuthLinkType.SIGNER).payload;

        var result = new JsonObject();

        for (int i = 0; i < chain.Length; i++)
            result[$"x-identity-auth-chain-{i}"] = JsonSerializer.Serialize(chain[i], AuthenticatorJsonContext.Default.AuthLink);

        var signedEntity = chain.First(l => l.type == AuthLinkType.ECDSA_SIGNED_ENTITY);
        var parts = signedEntity.payload.Split(':');
        var timestamp = parts[^2];

        result["x-identity-timestamp"] = timestamp;
        result["x-identity-metadata"] = parts[^1];

        return new LoginResult(result.ToJsonString(), walletAddress);
    }
}
