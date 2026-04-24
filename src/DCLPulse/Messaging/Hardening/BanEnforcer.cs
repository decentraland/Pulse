using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Applies a new ban-list snapshot: swaps <see cref="BanList" /> storage and evicts any
///     currently-connected peer whose wallet just became banned. Handshake-time enforcement
///     lives in <see cref="HandshakeHandler" /> and consults <see cref="BanList" /> directly —
///     this class handles the second enforcement window, mid-session eviction.
///     <para />
///     Threading: <see cref="Apply" /> is called from the <see cref="BansPollingHttpService" />
///     thread. It touches no <see cref="PeerState" />; it only enqueues disconnects through
///     <see cref="MessagePipe" />, which is the documented cross-thread entry point. The owning
///     worker picks up the Disconnected lifecycle event and performs its usual cleanup,
///     preserving the worker-shard isolation rule.
/// </summary>
public sealed class BanEnforcer(
    ILogger<BanEnforcer> logger,
    BanList banList,
    MessagePipe messagePipe,
    IdentityBoard identityBoard)
{
    /// <summary>
    ///     Swaps <paramref name="addresses" /> into <see cref="BanList" /> and evicts any
    ///     peer whose wallet appears in the new list but not the previous one. Increments
    ///     <see cref="PulseMetrics.Hardening.BANNED_REFUSED" /> once per eviction.
    /// </summary>
    public void Apply(IReadOnlyCollection<string> addresses)
    {
        IReadOnlyCollection<string> newlyBanned = banList.Replace(addresses);

        foreach (string wallet in newlyBanned)
        {
            if (!identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex peerIndex))
                continue;

            messagePipe.SendDisconnect(peerIndex, DisconnectReason.BANNED);
            PulseMetrics.Hardening.BANNED_REFUSED.Add(1);
            logger.LogInformation("Evicting peer {Peer} for wallet {Wallet} — newly banned", peerIndex, wallet);
        }
    }
}
