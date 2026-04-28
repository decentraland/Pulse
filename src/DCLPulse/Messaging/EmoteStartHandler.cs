using Decentraland.Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class EmoteStartHandler(
    PeerSnapshotPublisher snapshotPublisher,
    ILogger<EmoteStartHandler> logger,
    DiscreteEventRateLimiter rateLimiter,
    FieldValidator fieldValidator)
    : RuntimePacketHandlerBase<EmoteStartHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out PeerState? peerState))
            return;

        if (!rateLimiter.TryAccept(from, peerState))
            return;

        if (!fieldValidator.ValidateEmoteStart(from, peerState, message.EmoteStart))
            return;

        EmoteStart emoteStart = message.EmoteStart;
        uint? durationMs = emoteStart.HasDurationMs ? emoteStart.DurationMs : null;

        var emote = new PeerSnapshotPublisher.EmoteInput(emoteStart.EmoteId, DurationMs: durationMs);
        snapshotPublisher.PublishFromPlayerState(from, emoteStart.PlayerState, emote);

        logger.LogInformation("Peer {Peer} started emote {EmoteId}", from.Value, emoteStart.EmoteId);
    }
}
