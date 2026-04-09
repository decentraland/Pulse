using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class EmoteStartHandler(EmoteBoard emoteBoard, SnapshotBoard snapshotBoard, ITimeProvider timeProvider, ILogger<EmoteStartHandler> logger)
    : RuntimePacketHandlerBase<EmoteStartHandler>(logger), IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        if (SkipFromUnauthorizedPeer(peers, from, message, out _))
            return;

        EmoteStart emoteStart = message.EmoteStart;

        uint? durationMs = emoteStart.HasDurationMs ? emoteStart.DurationMs : null;
        uint now = timeProvider.MonotonicTime;

        emoteBoard.Start(from, emoteStart.EmoteId, now, durationMs);

        // Bump the snapshot seq so EmoteStarted carries a distinct sequence from the preceding delta
        if (snapshotBoard.TryRead(from, out PeerSnapshot current))
            snapshotBoard.Publish(from, current with { Seq = snapshotBoard.LastSeq(from) + 1, ServerTick = now });

        logger.LogInformation("Peer {Peer} started emote {EmoteId}", from.Value, emoteStart.EmoteId);
    }
}
