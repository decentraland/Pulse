namespace PulseTestClient.Bridge;

/// <summary>
///     Builds the <c>livekit:{url}?access_token={token}</c> string a client dials, which is the only
///     field of <c>IslandChangedMessage</c> unity-explorer actually reads.
/// </summary>
public interface IConnStringSource
{
    /// <summary>Credential-free one-liner for the startup log.</summary>
    string Description { get; }

    /// <summary>
    ///     Conn string for one assignment. <paramref name="room" /> is the LiveKit room name
    ///     (<c>island-{clusterId}</c>) and <paramref name="clusterId" /> the bare id behind it; the
    ///     two are separate parameters because a token is scoped to the room while the synthetic
    ///     stand-in is keyed by the id.
    /// </summary>
    string Build(string wallet, string room, string clusterId);
}
