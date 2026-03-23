namespace Pulse.Peers.Simulation;

/// <summary>
///     Shared emote state store indexed by <see cref="PeerIndex" />.
///     Tracks which peer is currently emoting.
///     <para />
///     Writer: the handler on whichever worker owns the peer.
///     Readers: simulation step on any worker, diffs against <see cref="PeerToPeerView.LastSentEmoteId" />.
/// </summary>
public class EmoteBoard(int maxPeers)
{
    private readonly string?[] emoteIds = new string?[maxPeers];
    private readonly uint[] startTicks = new uint[maxPeers];
    private readonly uint[] stopTicks = new uint[maxPeers];

    public void Start(PeerIndex id, string emoteId, uint serverTick)
    {
        Volatile.Write(ref startTicks[(int)id.Value], serverTick);
        Volatile.Write(ref emoteIds[(int)id.Value], emoteId);
    }

    public void Stop(PeerIndex id, uint serverTick)
    {
        Volatile.Write(ref stopTicks[(int)id.Value], serverTick);
        Volatile.Write(ref emoteIds[(int)id.Value], null);
    }

    public string? GetCurrentEmote(PeerIndex id) =>
        Volatile.Read(ref emoteIds[(int)id.Value]);

    public uint GetStartTick(PeerIndex id) =>
        Volatile.Read(ref startTicks[(int)id.Value]);

    public uint GetStopTick(PeerIndex id) =>
        Volatile.Read(ref stopTicks[(int)id.Value]);

    public void Remove(PeerIndex id) =>
        Volatile.Write(ref emoteIds[(int)id.Value], null);
}