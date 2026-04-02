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
        if (!peers.TryGetValue(from, out state) || state.ConnectionState != PeerConnectionState.AUTHENTICATED)
        {
            PulseMetrics.Transport.UNAUTH_MESSAGES_SKIPPED.Add(1);
            logger.LogWarning("A unauthenticated peer {Peer} sent a message {MessageCase}, skipped processing", from.Value, message.MessageCase);
            return true;
        }

        return false;
    }
}
