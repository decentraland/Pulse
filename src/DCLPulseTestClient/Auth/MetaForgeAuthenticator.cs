using System.Text.Json;
using System.Text.Json.Nodes;

namespace PulseTestClient.Auth;

public class MetaForgeAuthenticator : IAuthenticator
{
    public async Task<LoginResult> LoginAsync(string account, CancellationToken ct)
    {
        var output = await MetaForge.RunCommandAsync(
            $"account chain {account} --method connect --path / --metadata {{}} --skip-update-check --json", ct);

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

    public async Task<string> SignPayloadAsync(string account, string payload, CancellationToken ct)
    {
        // `account sign` signs the payload as-is; `account chain` would fold it into the signed-fetch
        // "method:path:timestamp:metadata" form, which a ws-connector challenge must not be. The payload
        // goes last because RunCommandAsync passes one argument string through to the process.
        string output = await MetaForge.RunCommandAsync(
            $"account sign {account} --skip-update-check --json --payload {payload}", ct);

        // RunCommandAsync raises a non-zero exit, so this only catches the residual case of a clean exit
        // that printed nothing — still worth naming, since the alternative is a JSON parse error.
        AuthLink[]? chain = string.IsNullOrWhiteSpace(output)
            ? null
            : JsonSerializer.Deserialize(output, AuthenticatorJsonContext.Default.AuthLinkArray);

        if (chain is not {Length: > 0})
            throw new PulseException($"metaforge account sign '{account}' produced no auth chain — check that the account exists and its identity is valid.");

        // A SIGNER link carries an empty signature, never a missing one — @dcl/schemas validates the
        // field as a string, and a chain restored from a stored identity can arrive without it.
        for (var i = 0; i < chain.Length; i++)
            chain[i].signature ??= "";

        // Re-serialize rather than forwarding stdout: MetaForge pretty-prints and RunCommandAsync only
        // strips the newlines, leaving the indentation in the string the server has to JSON.parse.
        return JsonSerializer.Serialize(chain, AuthenticatorJsonContext.Default.AuthLinkArray);
    }
}
