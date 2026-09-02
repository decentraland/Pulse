using System.Text;
using System.Text.Json;

namespace PulseTestClient;

/// <summary>
///     Reads the claims of the LiveKit access token inside a conn string, so a run can say whether the
///     token is one a client could actually use rather than only that one arrived.
///     <para />
///     A JWT's header and payload are base64url-encoded JSON, not encrypted, so this needs no key and
///     verifies no signature — it cannot tell a correctly signed token from a forged one. What it does
///     catch is the failure mode that looks identical to success from the outside: a token that
///     arrives but is expired, names another room, or was issued to another identity.
///     <para />
///     Nothing here returns the token or its signature. <see cref="Describe" /> is the only output, and
///     it carries claims only.
/// </summary>
public static class LiveKitToken
{
    /// <summary>
    ///     A one-line summary of the token in <paramref name="connStr" />, or why it could not be read.
    ///     <paramref name="expectedRoom" /> and <paramref name="expectedIdentity" /> are compared when
    ///     given, since a token for the wrong room is the failure a client sees as "island won't
    ///     connect".
    /// </summary>
    public static string Describe(string connStr, string? expectedRoom = null, string? expectedIdentity = null)
    {
        if (!TryExtractToken(connStr, out string token))
            return "no access_token in the conn string";

        string[] parts = token.Split('.');

        if (parts.Length < 2)
            return "access_token is not a JWT (expected header.payload.signature)";

        JsonElement payload;

        try { payload = JsonSerializer.Deserialize<JsonElement>(DecodeSegment(parts[1])); }
        catch (Exception e) { return $"access_token payload is not readable JSON: {e.Message}"; }

        var notes = new List<string>();

        string? room = TryGetVideoString(payload, "room");
        string? identity = TryGetString(payload, "sub");

        notes.Add($"room={room ?? "<none>"}");
        notes.Add($"identity={Shorten(identity)}");
        notes.Add(DescribeExpiry(payload));

        if (TryGetVideoBool(payload, "canPublish") is { } canPublish)
            notes.Add($"publish={canPublish}");

        // The API key identifies which credentials minted it — useful when two producers are live at
        // once. It is an identifier, not the signing secret, but it is still shortened.
        notes.Add($"key={Shorten(TryGetString(payload, "iss"))}");

        if (expectedRoom is not null && !string.Equals(room, expectedRoom, StringComparison.Ordinal))
            notes.Add($"MISMATCH: room is '{room}' but the island is '{expectedRoom}'");

        if (expectedIdentity is not null && identity is not null
            && !string.Equals(identity, expectedIdentity, StringComparison.OrdinalIgnoreCase))
            notes.Add($"MISMATCH: identity is '{Shorten(identity)}' but this bot is '{Shorten(expectedIdentity)}'");

        return string.Join(" ", notes);
    }

    /// <summary>
    ///     Splits a conn string into the LiveKit server URL and the access token. Validates neither —
    ///     that is <see cref="Describe" />'s job for the token, and the server's for the URL. False when
    ///     either half is missing.
    /// </summary>
    public static bool TryParse(string connStr, out string url, out string token)
    {
        url = string.Empty;

        if (!TryExtractToken(connStr, out token)) return false;

        // `livekit:wss://host?access_token=...`. The `livekit:` scheme names which transport the
        // adapter is, Decentraland-side, and is not part of the URL the SDK wants.
        const string SCHEME = "livekit:";
        int scheme = connStr.IndexOf(SCHEME, StringComparison.OrdinalIgnoreCase);
        string rest = scheme < 0 ? connStr : connStr[(scheme + SCHEME.Length)..];
        int query = rest.IndexOf('?');

        url = (query < 0 ? rest : rest[..query]).Trim();

        return url.Length > 0;
    }

    private static string DescribeExpiry(JsonElement payload)
    {
        if (payload.TryGetProperty("exp", out JsonElement exp) && exp.TryGetInt64(out long seconds))
        {
            DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            TimeSpan remaining = expiresAt - DateTimeOffset.UtcNow;

            return remaining > TimeSpan.Zero
                ? $"expires in {remaining.TotalMinutes:F0}m"
                : $"EXPIRED {(-remaining).TotalMinutes:F0}m ago";
        }

        return "no exp claim";
    }

    private static bool TryExtractToken(string connStr, out string token)
    {
        token = string.Empty;
        int at = connStr.IndexOf("access_token=", StringComparison.OrdinalIgnoreCase);

        if (at < 0) return false;

        string rest = connStr[(at + "access_token=".Length)..];
        int end = rest.IndexOfAny(['&', ' ', '"', '\'']);

        token = end < 0 ? rest : rest[..end];

        return token.Length > 0;
    }

    private static string? TryGetString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryGetVideoString(JsonElement payload, string name) =>
        payload.TryGetProperty("video", out JsonElement video) && video.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? TryGetVideoBool(JsonElement payload, string name) =>
        payload.TryGetProperty("video", out JsonElement video) && video.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>Ends and length only — enough to compare two values without reproducing either.</summary>
    private static string Shorten(string? value) =>
        string.IsNullOrEmpty(value) ? "<none>"
        : value.Length <= 12 ? value
        : $"{value[..6]}…{value[^4..]}";

    private static byte[] DecodeSegment(string segment)
    {
        string padded = segment.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
