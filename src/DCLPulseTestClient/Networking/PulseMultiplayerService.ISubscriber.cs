using Decentraland.Pulse;

namespace PulseTestClient.Networking;

public partial class PulseMultiplayerService
{
    private interface ISubscriber
    {
        bool TryNotify(ServerMessage message);
    }
}