namespace Pulse.Peers;

/// <summary>
///     The server keeps a small rolling history of snapshots per subject, indexed by seq. <br />
///     It enables performing "cheap" diffs over "old" sequences if newer ones have been lost by the target peer
/// </summary>
/// <param name="capacity"></param>
public struct PeerSnapshotHistory(int capacity)
{
    private readonly PeerSnapshot[] ring = new PeerSnapshot[capacity];

    public uint LastSeq;

    public void Store(PeerSnapshot snap)
    {
        LastSeq = snap.Seq;
        ring[snap.Seq % capacity] = snap;
    }

    public bool TryGet(uint seq, out PeerSnapshot snapshot)
    {
        snapshot = ring[seq % capacity];

        // guard against the ring having wrapped and overwritten it
        return snapshot.Seq == seq;
    }
}
