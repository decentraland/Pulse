using Decentraland.Pulse;
using System.Threading.Channels;

namespace PulseTestClient.Networking;

public partial class PulseMultiplayerService
{
    private class GenericSubscriber<T> : ISubscriber where T: class
    {
        private readonly ServerMessage.MessageOneofCase type;

        public Channel<T> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<T>(
            new UnboundedChannelOptions {SingleWriter = true, SingleReader = true});

        public GenericSubscriber(ServerMessage.MessageOneofCase type)
        {
            this.type = type;
        }

        public bool TryNotify(ServerMessage message)
        {
            if (message.MessageCase != type) return false;

            T? payload = TryGetPayload(message);

            return payload != null && Channel.Writer.TryWrite(payload);
        }

        private static T? TryGetPayload(ServerMessage message)
        {
            return message.MessageCase switch
            {
                ServerMessage.MessageOneofCase.Handshake => message.Handshake as T,
                ServerMessage.MessageOneofCase.EmoteStarted => message.EmoteStarted as T,
                ServerMessage.MessageOneofCase.EmoteStopped => message.EmoteStopped as T,
                ServerMessage.MessageOneofCase.PlayerJoined => message.PlayerJoined as T,
                ServerMessage.MessageOneofCase.PlayerLeft => message.PlayerLeft as T,
                ServerMessage.MessageOneofCase.PlayerProfileVersionAnnounced => message.PlayerProfileVersionAnnounced as T,
                ServerMessage.MessageOneofCase.PlayerStateDelta => message.PlayerStateDelta as T,
                ServerMessage.MessageOneofCase.PlayerStateFull => message.PlayerStateFull as T,
                _ => null
            };
        }
    }

    /// <summary>
    ///     Routes all message types into a single channel preserving global arrival order.
    /// </summary>
    private class BroadcastSubscriber : ISubscriber
    {
        public Channel<ServerMessage> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<ServerMessage>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        public bool TryNotify(ServerMessage message) =>
            Channel.Writer.TryWrite(message);
    }
}