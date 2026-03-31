using System.Text.Json.Serialization;

namespace Pulse.Messaging;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class HandshakeJsonContext : JsonSerializerContext;
