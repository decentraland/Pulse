namespace Pulse.Peers.Simulation;

public class ProfileBoard(int maxPeers)
{
    private readonly int[] versions = new int[maxPeers];

    public void Set(PeerIndex id, int version)
    {
        Volatile.Write(ref versions[(int)id.Value], version);
    }

    public int Get(PeerIndex id) =>
        Volatile.Read(ref versions[(int)id.Value]);

    public void Remove(PeerIndex id)
    {
        Volatile.Write(ref versions[(int)id.Value], 0);
    }
}
