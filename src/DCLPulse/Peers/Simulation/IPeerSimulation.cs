namespace Pulse.Peers.Simulation;

public interface IPeerSimulation
{
    public uint BaseTickMs { get; }

    public void SimulateTick(Dictionary<PeerIndex, PeerState> peers, uint tickCounter);

    public void RemoveObserver(PeerIndex observerId);
}
