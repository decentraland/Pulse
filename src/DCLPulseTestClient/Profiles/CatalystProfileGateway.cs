using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseTestClient.Profiles;

public class CatalystProfileGateway : IProfileGateway, IDisposable
{
    private readonly HttpClient httpClient = new ();
    private const string CatalystUrl = "https://peer.decentraland.org/content/entities/active";

    public async Task<Profile> GetAsync(string walletAddress, CancellationToken ct)
    {
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            CatalystUrl,
            new { pointers = new[] { walletAddress.ToLowerInvariant() } },
            ct);

        response.EnsureSuccessStatusCode();

        CatalystEntity[]? entities = await response.Content.ReadFromJsonAsync(CatalystJsonContext.Default.CatalystEntityArray, ct);

        if (entities is null || entities.Length == 0)
            throw new PulseException($"No profile found on Catalyst for {walletAddress}");

        CatalystAvatarEntry avatar = entities[0].Metadata.Avatars[0];
        string[] emotes = avatar.Avatar.Emotes.Select(e => e.Urn).ToArray();

        return new Profile(new Web3Address(avatar.EthAddress), avatar.Version, emotes);
    }

    public void Dispose() =>
        httpClient.Dispose();
}

internal class CatalystEntity
{
    [JsonPropertyName("metadata")] public CatalystMetadata Metadata { get; set; } = null!;
}

internal class CatalystMetadata
{
    [JsonPropertyName("avatars")] public List<CatalystAvatarEntry> Avatars { get; set; } = [];
}

internal class CatalystAvatarEntry
{
    [JsonPropertyName("ethAddress")] public string EthAddress { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("avatar")] public CatalystAvatarData Avatar { get; set; } = null!;
}

internal class CatalystAvatarData
{
    [JsonPropertyName("emotes")] public List<CatalystEmoteEntry> Emotes { get; set; } = [];
}

internal class CatalystEmoteEntry
{
    [JsonPropertyName("urn")] public string Urn { get; set; } = "";
}

[JsonSerializable(typeof(CatalystEntity[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class CatalystJsonContext : JsonSerializerContext;
