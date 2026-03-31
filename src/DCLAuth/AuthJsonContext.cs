using System.Text.Json.Serialization;

namespace DCL.Auth;

[JsonSerializable(typeof(AuthLink))]
[JsonSerializable(typeof(List<AuthLink>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AuthJsonContext : JsonSerializerContext;
