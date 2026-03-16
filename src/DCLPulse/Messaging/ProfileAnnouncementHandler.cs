using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Messaging;

public class ProfileAnnouncementHandler(ProfileBoard profileBoard) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        profileBoard.Set(from, message.ProfileAnnouncement.Version);
    }
}
