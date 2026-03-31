using System.Text.Json.Serialization;

namespace PulseTestClient.Auth;

[JsonSerializable(typeof(AuthLink))]
[JsonSerializable(typeof(AuthLink[]))]
[JsonSourceGenerationOptions(
    IncludeFields = true,
    Converters = [typeof(JsonStringEnumConverter<AuthLinkType>)])]
internal partial class AuthenticatorJsonContext : JsonSerializerContext;
