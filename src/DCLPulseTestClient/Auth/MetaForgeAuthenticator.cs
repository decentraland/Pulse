using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PulseTestClient.Auth;

public class MetaForgeAuthenticator : IAuthenticator
{
    public async Task<string> LoginAsync(string account, CancellationToken ct)
    {
        await MetaForge.RunCommandAsync($"account create {account} --skip-update-check --skip-auto-login", ct);
        
        var output = await MetaForge.RunCommandAsync(
            $"account chain {account} --method connect --path / --metadata {{}} --skip-update-check", ct);

        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            IncludeFields = true
        };

        var chain = JsonSerializer.Deserialize<AuthLink[]>(output, options)!;
        var result = new JsonObject();

        for (int i = 0; i < chain.Length; i++)
            result[$"x-identity-auth-chain-{i}"] = JsonSerializer.Serialize(chain[i], options);

        var signedEntity = chain.First(l => l.type == AuthLinkType.ECDSA_SIGNED_ENTITY);
        var parts = signedEntity.payload.Split(':');
        var timestamp = parts[^2];

        result["x-identity-timestamp"] = timestamp;
        result["x-identity-metadata"] = parts[^1];

        return result.ToJsonString();
    }
}