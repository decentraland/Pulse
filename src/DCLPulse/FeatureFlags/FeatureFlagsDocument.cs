using System.Text.Json.Serialization;

namespace Pulse.FeatureFlags;

/// <summary>
///     The Unleash document served at <c>{AppName}.json</c>: a flag-name to enabled map plus a
///     flag-name to variant map. Keys arrive prefixed by the application name;
///     <see cref="FeatureFlagsClient" /> strips that prefix.
/// </summary>
public sealed class FeatureFlagsDocument
{
    [JsonPropertyName("flags")]
    public Dictionary<string, bool>? Flags { get; set; }

    [JsonPropertyName("variants")]
    public Dictionary<string, FeatureFlagVariant>? Variants { get; set; }
}

/// <summary>
///     The single variant resolved for a flag. <see cref="Name" /> is the variant the server was
///     assigned; only the one named <c>configuration</c> carries configuration keys.
/// </summary>
public sealed class FeatureFlagVariant
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("payload")]
    public FeatureFlagVariantPayload? Payload { get; set; }
}

/// <summary>
///     A variant's payload. Unleash always transports the body as a string, so a
///     <c>type: "json"</c> payload holds JSON text that needs a second parse.
/// </summary>
public sealed class FeatureFlagVariantPayload
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
