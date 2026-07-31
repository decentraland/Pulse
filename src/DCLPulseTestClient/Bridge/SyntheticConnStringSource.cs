namespace PulseTestClient.Bridge;

/// <summary>
///     Hermetic conn strings: the real <c>livekit:{url}?access_token={token}</c> shape carrying a
///     host that cannot resolve and a token that is not a credential. Assertions can match the
///     format without CI holding a LiveKit account, and a client that mistakenly dials one fails to
///     connect rather than reaching somebody's real deployment.
/// </summary>
public sealed class SyntheticConnStringSource : IConnStringSource
{
    // .invalid is reserved by RFC 2606 and is guaranteed never to resolve.
    private const string HOST = "wss://stub.invalid";

    public string Description =>
        $"synthetic conn strings against {HOST}, no credentials needed";

    /// <summary>
    ///     Keyed by cluster id rather than room name so the stand-in token reads as the cluster the
    ///     assertion is about, and stays stable if the room naming ever gains a suffix.
    /// </summary>
    public string Build(string wallet, string room, string clusterId) =>
        $"livekit:{HOST}?access_token=stub-{clusterId}";
}
