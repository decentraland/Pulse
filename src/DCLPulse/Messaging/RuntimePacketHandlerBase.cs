using Decentraland.Pulse;
using Pulse.Metrics;
using Pulse.Peers;
using System.Diagnostics.CodeAnalysis;

namespace Pulse.Messaging;

public class RuntimePacketHandlerBase<T> where T: RuntimePacketHandlerBase<T>
{
    protected readonly ILogger<T> logger;

    protected RuntimePacketHandlerBase(ILogger<T> logger)
    {
        this.logger = logger;
    }

    protected bool SkipFromUnauthorizedPeer(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message, [MaybeNullWhen(true)] out PeerState state)
    {
        if (!peers.TryGetValue(from, out state))
        {
            // No state at all — genuinely unknown peer.
            PulseMetrics.Transport.UNAUTH_MESSAGES_SKIPPED.Add(1);
            logger.LogWarning("Unknown peer {Peer} sent {MessageCase}, skipped", from.Value, message.MessageCase);
            return true;
        }

        switch (state.ConnectionState)
        {
            case PeerConnectionState.AUTHENTICATED:
                return false;

            case PeerConnectionState.PENDING_DISCONNECT:
            case PeerConnectionState.DISCONNECTING:
                // Queued messages from a peer we already condemned — silent skip, no warning.
                // Bumping UNAUTH_MESSAGES_SKIPPED here would mask real unauth attacks behind
                // the noise of in-flight messages racing the disconnect.
                state = null;
                return true;

            default:
                // PENDING_AUTH / NONE — truly unauthenticated, warn.
                PulseMetrics.Transport.UNAUTH_MESSAGES_SKIPPED.Add(1);
                logger.LogWarning("Unauthenticated peer {Peer} sent {MessageCase}, skipped", from.Value, message.MessageCase);
                state = null;
                return true;
        }
    }
}
