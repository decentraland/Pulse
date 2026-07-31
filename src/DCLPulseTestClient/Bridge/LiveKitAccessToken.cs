using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PulseTestClient.Bridge;

/// <summary>
///     A LiveKit access token, written out by hand because this project references no LiveKit SDK.
///     Nothing exotic: a standard HS256 JWT signed with the API secret, whose <c>video</c> claim
///     carries the room grant LiveKit reads.
///     <para />
///     The claims mirror what comms-gatekeeper's cluster path mints — the wallet as <c>sub</c>, the
///     API key as <c>iss</c>, a five-minute window, and a grant that joins exactly one room with
///     data and microphone publishing. Kept deliberately close to that shape: a token this stub
///     issues has to be accepted by the same server the real service talks to.
/// </summary>
public static class LiveKitAccessToken
{
    /// <summary>
    ///     Signs a join grant for <paramref name="room" /> as <paramref name="identity" />. The
    ///     returned string is a credential — never log it, never write it to a fixture.
    /// </summary>
    public static string Mint(string apiKey, string apiSecret, string identity, string room, TimeSpan ttl)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var signingInput = $"{EncodeHeader()}.{EncodePayload(apiKey, identity, room, now, now + ttl)}";

        byte[] signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(apiSecret),
            Encoding.UTF8.GetBytes(signingInput));

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static string EncodeHeader()
    {
        var buffer = new ArrayBufferWriter<byte>();

        // Disposed before the span is read: the writer buffers, so an unflushed one yields nothing.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", "HS256");
            writer.WriteString("typ", "JWT");
            writer.WriteEndObject();
        }

        return Base64Url.EncodeToString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Written through <see cref="Utf8JsonWriter" /> rather than interpolated: the room name
    ///     reaches here from a broker message, and hand-built JSON would let a quote in it rewrite
    ///     the grant.
    /// </summary>
    private static string EncodePayload(
        string apiKey,
        string identity,
        string room,
        DateTimeOffset notBefore,
        DateTimeOffset expires)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteString("iss", apiKey);
            writer.WriteString("sub", identity);
            writer.WriteNumber("nbf", notBefore.ToUnixTimeSeconds());
            writer.WriteNumber("exp", expires.ToUnixTimeSeconds());

            writer.WriteStartObject("video");
            writer.WriteString("room", room);
            writer.WriteBoolean("roomJoin", true);
            writer.WriteBoolean("roomList", false);
            writer.WriteBoolean("canPublish", true);
            writer.WriteBoolean("canSubscribe", true);
            writer.WriteBoolean("canPublishData", true);
            writer.WriteBoolean("canUpdateOwnMetadata", true);

            // The real service passes an empty cast list for cluster assignments, which narrows
            // publishing to the microphone rather than leaving every source open.
            writer.WriteStartArray("canPublishSources");
            writer.WriteStringValue("microphone");
            writer.WriteEndArray();

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Base64Url.EncodeToString(buffer.WrittenSpan);
    }
}
