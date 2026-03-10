using Decentraland.Pulse;
using Pulse.Peers;

namespace Pulse.Messaging;

public interface IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message);
}
