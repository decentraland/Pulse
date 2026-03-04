using System.Text.Json.Serialization;

namespace DCL.Auth;

public sealed record AuthLink(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature
);
