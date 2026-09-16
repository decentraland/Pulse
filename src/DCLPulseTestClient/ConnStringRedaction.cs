using System.Text.RegularExpressions;

namespace PulseTestClient;

/// <summary>
///     The one place a LiveKit token is hidden. Every conn string this process writes down goes
///     through <see cref="Redact" />.
/// </summary>
/// <remarks>
///     A conn string is <c>livekit:{url}?access_token={jwt}</c>, and that JWT is a live credential
///     for as long as it is valid. comms-gatekeeper mints a real one on every assignment, so there
///     is no run in which printing it unredacted would be safe.
/// </remarks>
public static partial class ConnStringRedaction
{
    private const string REPLACEMENT = "access_token=<redacted>";

    /// <summary>Replaces the value of every <c>access_token</c> parameter with a placeholder.</summary>
    public static string Redact(string text) =>
        TokenPattern().Replace(text, REPLACEMENT);

    // Stops at the first separator so the token goes and the rest of the line — a following query
    // parameter, or the trailing text of a log message — survives.
    [GeneratedRegex(@"access_token=[^\s&""']*", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();
}
