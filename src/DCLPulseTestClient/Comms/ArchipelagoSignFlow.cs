using Decentraland.Kernel.Comms.V3;
using PulseTestClient.Auth;

namespace PulseTestClient.Comms;

/// <summary>
///     The ws-connector handshake: challenge request → challenge response → signed challenge → welcome.
///     Signing is delegated to MetaForge through <see cref="IAuthenticator" />, so no key material ever
///     reaches this process.
/// </summary>
public sealed class ArchipelagoSignFlow
{
    private const string CHALLENGE_PREFIX = "dcl-";

    // Mirrors the server's HANDSHAKE_TIMEOUT default. ws-connector answers a slow stage by dropping the
    // socket, so failing first is what turns that silent close into an error that names the stage.
    private static readonly TimeSpan DEFAULT_STAGE_TIMEOUT = TimeSpan.FromSeconds(60);

    private readonly ICommsConnection connection;
    private readonly IAuthenticator authenticator;
    private readonly string account;
    private readonly TimeSpan stageTimeout;

    /// <param name="account">MetaForge account name to sign with — not the wallet address.</param>
    public ArchipelagoSignFlow(ICommsConnection connection, IAuthenticator authenticator, string account, TimeSpan? stageTimeout = null)
    {
        this.connection = connection;
        this.authenticator = authenticator;
        this.account = account;
        this.stageTimeout = stageTimeout ?? DEFAULT_STAGE_TIMEOUT;
    }

    /// <summary>
    ///     Connects and runs the handshake to completion.
    /// </summary>
    /// <param name="url">
    ///     A ws-connector URL, or a realm's raw comms adapter string — see <see cref="AdapterAddress" />.
    /// </param>
    /// <returns>The welcomed peer id — the address ws-connector actually registered.</returns>
    public async Task<string> ConnectAsync(string url, string address, CancellationToken ct)
    {
        // Refined here rather than at the call site so no caller can skip it, and so the log line below
        // reports the URL actually dialled instead of whatever was configured.
        string adapterUrl = AdapterAddress.Refine(url);

        // ws-connector keys its peer registry and the NATS subject it publishes to on the lowercased
        // address. One upper-case nibble here and the island never reaches us, on a socket that looks
        // perfectly healthy the whole time — hence the log line naming exactly what we registered.
        string normalized = address.ToLowerInvariant();

        Console.WriteLine($"[ws-connector] Connecting to {adapterUrl} as {normalized}");

        await connection.ConnectAsync(adapterUrl, ct);

        ServerPacket challengeReply = await ExchangeAsync(
            new ClientPacket {ChallengeRequest = new ChallengeRequestMessage {Address = normalized}},
            "challenge request", ct);

        if (challengeReply.MessageCase != ServerPacket.MessageOneofCase.ChallengeResponse)
            throw new PulseException($"ws-connector answered the challenge request with {challengeReply.MessageCase} instead of ChallengeResponse.");

        ChallengeResponseMessage challenge = challengeReply.ChallengeResponse;

        // Only sign what ws-connector is supposed to have produced ('dcl-' + 32 random bytes as hex).
        // Signing an arbitrary server-supplied string would turn this bot into a signing oracle.
        if (!challenge.ChallengeToSign.StartsWith(CHALLENGE_PREFIX, StringComparison.Ordinal))
            throw new PulseException($"Refusing to sign '{challenge.ChallengeToSign}': a ws-connector challenge starts with '{CHALLENGE_PREFIX}'.");

        if (challenge.AlreadyConnected)
            Console.WriteLine($"[ws-connector] {normalized} already has a session — completing this handshake kicks it with KR_NEW_SESSION.");

        // Verbatim: the server validates the signature against the exact string it generated.
        string authChainJson = await authenticator.SignPayloadAsync(account, challenge.ChallengeToSign, ct);

        ServerPacket welcomeReply = await ExchangeAsync(
            new ClientPacket {SignedChallenge = new SignedChallengeMessage {AuthChainJson = authChainJson}},
            "signed challenge", ct);

        // Every server-side rejection — deny list, ban, invalid chain — ends in a plain socket close,
        // which surfaces out of ReceiveAsync as a PulseException rather than as a packet. Anything that
        // does arrive and is not a Welcome is a rejection too.
        if (welcomeReply.MessageCase != ServerPacket.MessageOneofCase.Welcome)
            throw new PulseException($"ws-connector rejected {normalized}: expected Welcome, got {welcomeReply.MessageCase}.");

        string peerId = welcomeReply.Welcome.PeerId;

        if (!string.Equals(peerId, normalized, StringComparison.Ordinal))
            Console.WriteLine($"[ws-connector] Welcomed as '{peerId}' but we registered '{normalized}' — routing follows the server's spelling.");

        Console.WriteLine($"[ws-connector] Welcome received, peer id {peerId}");

        return peerId;
    }

    private async Task<ServerPacket> ExchangeAsync(ClientPacket request, string stage, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(stageTimeout);

        try
        {
            await connection.SendAsync(request, timeout.Token);
            return await connection.ReceiveAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PulseException($"ws-connector did not answer the {stage} within {stageTimeout.TotalSeconds:0} s.");
        }
    }
}
