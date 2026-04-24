namespace Pulse.Messaging.Hardening;

public sealed class BansOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:Bans";

    /// <summary>
    ///     How often the poller fetches the latest ban list from comms-gatekeeper. Zero disables
    ///     the poller — handshake-time enforcement still runs against whatever is in
    ///     <see cref="BanList" /> (empty by default), so the whole feature becomes a
    ///     constant-time no-op when the poller never runs.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    ///     HTTP request timeout for each gatekeeper poll, in seconds. Zero means no timeout.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 10;
}
