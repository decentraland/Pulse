using System.Text.RegularExpressions;

namespace PulseTestClient;

/// <summary>
///     The one place a LiveKit token is hidden. Every conn string this process writes down goes
///     through <see cref="Redact" /> — the ones the bridge mints and the ones the comms channel
///     observes — synthetic ones included, so the default mode exercises the path the credentialed
///     mode depends on instead of leaving it first used on the day a real secret is in the string.
/// </summary>
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
