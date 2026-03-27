using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PulseTestClient.Profiles;

public class MetaForgeProfileGateway : IProfileGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<Profile> GetAsync(string account, CancellationToken ct)
    {
        var json = await MetaForge.RunCommandAsync($"account info {account} --json", ct);

        var response = JsonSerializer.Deserialize<ProfileResponse>(json, JsonOptions)!;
        var avatar = response.Metadata.Avatars[0];

        var emotes = avatar.Avatar.Emotes
            .Select(e => e.Urn)
            .ToArray();

        return new Profile(new Web3Address(avatar.EthAddress), avatar.Version, emotes);
    }
}

file class ProfileResponse
{
    [JsonPropertyName("metadata")] public ProfileMetadata Metadata { get; set; } = null!;
}

file class ProfileMetadata
{
    [JsonPropertyName("avatars")] public List<AvatarEntry> Avatars { get; set; } = [];
}

file class AvatarEntry
{
    [JsonPropertyName("ethAddress")] public string EthAddress { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("avatar")] public AvatarData Avatar { get; set; } = null!;
}

file class AvatarData
{
    [JsonPropertyName("emotes")] public List<EmoteEntry> Emotes { get; set; } = [];
}

file class EmoteEntry
{
    [JsonPropertyName("urn")] public string Urn { get; set; } = "";
}
