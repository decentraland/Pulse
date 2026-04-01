using Decentraland.Pulse;
using PulseTestClient.Networking;

namespace PulseTestClient;

public static class ServerEventHandler
{
    public static void SubscribeAll(BotSession bot, CancellationToken ct)
    {
        _ = Task.WhenAll(
            OnDeltaState(bot, ct),
            OnFullState(bot, ct),
            OnPeerJoined(bot, ct),
            OnPeerLeft(bot, ct),
            OnEmoteStarted(bot, ct),
            OnEmoteStopped(bot, ct),
            OnProfileAnnounced(bot, ct));
    }

    private static async Task OnDeltaState(BotSession bot, CancellationToken ct)
    {
        await foreach (PlayerStateDeltaTier0 delta in bot.Service.SubscribeAsync<PlayerStateDeltaTier0>(
                           ServerMessage.MessageOneofCase.PlayerStateDelta, ct))
        {
            uint subjectId = delta.SubjectId;

            // Already waiting for a full state — drop silently
            if (bot.PendingResyncs.Contains(subjectId))
                continue;

            if (bot.KnownSeqBySubject.TryGetValue(subjectId, out uint lastSeq) && delta.BaselineSeq != lastSeq)
            {
                Console.WriteLine($"[{bot.AccountName}] Seq gap for subject {subjectId}: expected {lastSeq}, got {delta.BaselineSeq}. Requesting resync.");

                bot.PendingResyncs.Add(subjectId);

                bot.Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
                {
                    Resync = new ResyncRequest { SubjectId = subjectId, KnownSeq = lastSeq },
                }, ITransport.PacketMode.RELIABLE));

                continue;
            }

            bot.KnownSeqBySubject[subjectId] = delta.NewSeq;
        }
    }

    private static async Task OnFullState(BotSession bot, CancellationToken ct)
    {
        await foreach (PlayerStateFull full in bot.Service.SubscribeAsync<PlayerStateFull>(
                           ServerMessage.MessageOneofCase.PlayerStateFull, ct))
        {
            bot.KnownSeqBySubject[full.SubjectId] = full.Sequence;
            bot.PendingResyncs.Remove(full.SubjectId);
            Console.WriteLine($"[{bot.AccountName}] Full state for subject {full.SubjectId}, seq={full.Sequence}");
        }
    }

    private static async Task OnPeerJoined(BotSession bot, CancellationToken ct)
    {
        await foreach (PlayerJoined joined in bot.Service.SubscribeAsync<PlayerJoined>(
                           ServerMessage.MessageOneofCase.PlayerJoined, ct))
        {
            bot.PeerAddresses[joined.State.SubjectId] = new Web3Address(joined.UserId);
            bot.KnownSeqBySubject[joined.State.SubjectId] = joined.State.Sequence;
            Console.WriteLine($"[{bot.AccountName}] Player joined: {joined.UserId}");
        }
    }

    private static async Task OnPeerLeft(BotSession bot, CancellationToken ct)
    {
        await foreach (PlayerLeft left in bot.Service.SubscribeAsync<PlayerLeft>(
                           ServerMessage.MessageOneofCase.PlayerLeft, ct))
        {
            bot.PeerAddresses.TryGetValue(left.SubjectId, out Web3Address address);
            bot.KnownSeqBySubject.Remove(left.SubjectId);
            bot.PendingResyncs.Remove(left.SubjectId);
            bot.PeerAddresses.Remove(left.SubjectId);
            Console.WriteLine($"[{bot.AccountName}] Player left: {address}");
        }
    }

    private static async Task OnEmoteStarted(BotSession bot, CancellationToken ct)
    {
        await foreach (EmoteStarted emote in bot.Service.SubscribeAsync<EmoteStarted>(
                           ServerMessage.MessageOneofCase.EmoteStarted, ct))
        {
            bot.PeerAddresses.TryGetValue(emote.SubjectId, out Web3Address address);
            Console.WriteLine($"[{bot.AccountName}] Emote started {emote.EmoteId} from {address}");
        }
    }

    private static async Task OnEmoteStopped(BotSession bot, CancellationToken ct)
    {
        await foreach (EmoteStopped emote in bot.Service.SubscribeAsync<EmoteStopped>(
                           ServerMessage.MessageOneofCase.EmoteStopped, ct))
        {
            bot.PeerAddresses.TryGetValue(emote.SubjectId, out Web3Address address);
            Console.WriteLine($"[{bot.AccountName}] Emote stopped {emote.Reason} from {address}");
        }
    }

    private static async Task OnProfileAnnounced(BotSession bot, CancellationToken ct)
    {
        await foreach (PlayerProfileVersionsAnnounced announcement in
                       bot.Service.SubscribeAsync<PlayerProfileVersionsAnnounced>(
                           ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced, ct))
        {
            bot.PeerAddresses.TryGetValue(announcement.SubjectId, out Web3Address address);
            Console.WriteLine($"[{bot.AccountName}] Profile announced: v{announcement.Version} from {address}");
        }
    }
}
