using DCL.Auth;
using Google.Protobuf;
using System.Text.Json;

namespace Pulse.Messaging;

/// <summary>
///     The auth-chain half of a handshake, shared by <see cref="HandshakeHandler" /> and
///     <see cref="SceneListenerHandshakeHandler" />: parse the signed-fetch headers JSON,
///     rebuild the expected connect payload, and validate the ECDSA chain. Returns
///     <c>null</c> when the JSON cannot be parsed; throws (same exceptions as
///     <see cref="AuthChainValidator.Validate" />) when the chain is invalid — callers
///     translate both into a handshake reject.
/// </summary>
public sealed class HandshakeAuthenticator(AuthChainValidator authChainValidator)
{
    public readonly record struct AuthResult(string UserAddress, string Timestamp);

    public AuthResult? Authenticate(ByteString authChain)
    {
        string authChainJson = authChain.ToStringUtf8();
        Dictionary<string, string>? headers = JsonSerializer.Deserialize(authChainJson, HandshakeJsonContext.Default.DictionaryStringString);

        if (headers == null)
            return null;

        IReadOnlyList<AuthLink> chain = AuthChainParser.ParseFromSignedFetchHeaders(headers);

        string timestamp = string.Empty;
        string metadata = string.Empty;

        foreach (KeyValuePair<string, string> kv in headers)
        {
            if (kv.Key.Equals("x-identity-timestamp", StringComparison.OrdinalIgnoreCase))
                timestamp = kv.Value;

            if (kv.Key.Equals("x-identity-metadata", StringComparison.OrdinalIgnoreCase))
                metadata = kv.Value;
        }

        string expectedPayload = SignedFetch.BuildSignedFetchPayload("connect", "/", timestamp, metadata);
        AuthChainValidationResult result = authChainValidator.Validate(chain, expectedPayload);

        return new AuthResult(result.UserAddress, timestamp);
    }
}
