using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseTestClient.Profiles;

public class MetaForgeProfileGateway : IProfileGateway
{
    public async Task<Profile> GetAsync(string account, CancellationToken ct)
    {
        var json = await MetaForge.RunCommandAsync($"account info {account} --json", ct);

        ProfileResponse response = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProfileResponse)!;
        var avatar = response.Metadata.Avatars[0];

        var emotes = avatar.Avatar.Emotes
            .Select(e => e.Urn)
            .ToArray();

        return new Profile(new Web3Address(avatar.EthAddress), avatar.Version, emotes);
    }
}

internal class ProfileResponse
{
    [JsonPropertyName("metadata")] public ProfileMetadata Metadata { get; set; } = null!;
}

internal class ProfileMetadata
{
    [JsonPropertyName("avatars")] public List<AvatarEntry> Avatars { get; set; } = [];
}

internal class AvatarEntry
{
    [JsonPropertyName("ethAddress")] public string EthAddress { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("avatar")] public AvatarData Avatar { get; set; } = null!;
}

internal class AvatarData
{
    [JsonPropertyName("emotes")] public List<EmoteEntry> Emotes { get; set; } = [];
}

internal class EmoteEntry
{
    [JsonPropertyName("urn")] public string Urn { get; set; } = "";
}

[JsonSerializable(typeof(ProfileResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ProfileJsonContext : JsonSerializerContext;
