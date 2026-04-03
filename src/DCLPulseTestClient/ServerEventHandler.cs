using Decentraland.Pulse;
using PulseTestClient.Networking;

namespace PulseTestClient;

public static class ServerEventHandler
{
    public static async Task ProcessAll(BotSession bot, CancellationToken ct)
    {
        await foreach (ServerMessage message in bot.Service.SubscribeAllAsync(ct,
                           ServerMessage.MessageOneofCase.PlayerStateDelta,
                           ServerMessage.MessageOneofCase.PlayerStateFull,
                           ServerMessage.MessageOneofCase.PlayerJoined,
                           ServerMessage.MessageOneofCase.PlayerLeft,
                           ServerMessage.MessageOneofCase.EmoteStarted,
                           ServerMessage.MessageOneofCase.EmoteStopped,
                           ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced))
        {
            switch (message.MessageCase)
            {
                case ServerMessage.MessageOneofCase.PlayerStateDelta:
                    OnDelta(bot, message.PlayerStateDelta);
                    break;
                case ServerMessage.MessageOneofCase.PlayerStateFull:
                    OnFull(bot, message.PlayerStateFull);
                    break;
                case ServerMessage.MessageOneofCase.PlayerJoined:
                    OnJoined(bot, message.PlayerJoined);
                    break;
                case ServerMessage.MessageOneofCase.PlayerLeft:
                    OnLeft(bot, message.PlayerLeft);
                    break;
                case ServerMessage.MessageOneofCase.EmoteStarted:
                    OnEmoteStarted(bot, message.EmoteStarted);
                    break;
                case ServerMessage.MessageOneofCase.EmoteStopped:
                    OnEmoteStopped(bot, message.EmoteStopped);
                    break;
                case ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced:
                    OnProfileAnnounced(bot, message.PlayerProfileVersionAnnounced);
                    break;
            }
        }
    }

    private static void OnDelta(BotSession bot, PlayerStateDeltaTier0 delta)
    {
        uint subjectId = delta.SubjectId;

        if (bot.PendingResyncs.Contains(subjectId))
            return;

        if (bot.KnownSeqBySubject.TryGetValue(subjectId, out uint lastSeq) && delta.BaselineSeq != lastSeq)
        {
            Console.WriteLine($"[{bot.AccountName}] Seq gap for subject {subjectId}: expected {lastSeq}, got {delta.BaselineSeq}. Requesting resync.");

            bot.PendingResyncs.Add(subjectId);

            bot.Pipe.Send(new MessagePipe.OutgoingMessage(new ClientMessage
            {
                Resync = new ResyncRequest { SubjectId = subjectId, KnownSeq = lastSeq },
            }, ITransport.PacketMode.RELIABLE));

            return;
        }

        bot.KnownSeqBySubject[subjectId] = delta.NewSeq;
    }

    private static void OnFull(BotSession bot, PlayerStateFull full)
    {
        bot.KnownSeqBySubject[full.SubjectId] = full.Sequence;
        bot.PendingResyncs.Remove(full.SubjectId);
        Console.WriteLine($"[{bot.AccountName}] Full state for subject {full.SubjectId}, seq={full.Sequence}");
    }

    private static void OnJoined(BotSession bot, PlayerJoined joined)
    {
        bot.PeerAddresses[joined.State.SubjectId] = new Web3Address(joined.UserId);
        bot.KnownSeqBySubject[joined.State.SubjectId] = joined.State.Sequence;
        Console.WriteLine($"[{bot.AccountName}] Player joined: {joined.UserId}");
    }

    private static void OnLeft(BotSession bot, PlayerLeft left)
    {
        bot.PeerAddresses.TryGetValue(left.SubjectId, out Web3Address address);
        bot.KnownSeqBySubject.Remove(left.SubjectId);
        bot.PendingResyncs.Remove(left.SubjectId);
        bot.PeerAddresses.Remove(left.SubjectId);
        Console.WriteLine($"[{bot.AccountName}] Player left: {address}");
    }

    private static void OnEmoteStarted(BotSession bot, EmoteStarted emote)
    {
        bot.PeerAddresses.TryGetValue(emote.SubjectId, out Web3Address address);
        Console.WriteLine($"[{bot.AccountName}] Emote started {emote.EmoteId} from {address}");
    }

    private static void OnEmoteStopped(BotSession bot, EmoteStopped emote)
    {
        bot.PeerAddresses.TryGetValue(emote.SubjectId, out Web3Address address);
        Console.WriteLine($"[{bot.AccountName}] Emote stopped {emote.Reason} from {address}");
    }

    private static void OnProfileAnnounced(BotSession bot, PlayerProfileVersionsAnnounced announcement)
    {
        bot.PeerAddresses.TryGetValue(announcement.SubjectId, out Web3Address address);
        Console.WriteLine($"[{bot.AccountName}] Profile announced: v{announcement.Version} from {address}");
    }
}
