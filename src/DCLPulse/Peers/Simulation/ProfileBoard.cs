using System.Collections.Concurrent;

namespace Pulse.Peers.Simulation;

public class ProfileBoard(int maxPeers)
{
    private readonly int[] versions = new int[maxPeers];
    private readonly ConcurrentDictionary<PeerIndex, bool> announcements = new ();

    public void Set(PeerIndex id, int version)
    {
        if (Get(id) == version) return;
        Volatile.Write(ref versions[(int)id.Value], version);
        announcements[id] = true;
    }

    public int Get(PeerIndex id) =>
        Volatile.Read(ref versions[(int)id.Value]);

    public void Clear(PeerIndex id) =>
        Volatile.Write(ref versions[(int)id.Value], 0);

    public bool HasBeenRecentlyAnnounced(PeerIndex id) =>
        announcements.GetValueOrDefault(id, false);

    public void ClearAnnouncements() =>
        announcements.Clear();
}
