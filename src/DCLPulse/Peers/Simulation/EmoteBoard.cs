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

    public void Start(PeerIndex id, string emoteId) =>
        Volatile.Write(ref emoteIds[(int)id.Value], emoteId);

    public void Stop(PeerIndex id) =>
        Volatile.Write(ref emoteIds[(int)id.Value], null);

    public string? GetCurrentEmote(PeerIndex id) =>
        Volatile.Read(ref emoteIds[(int)id.Value]);

    public void Remove(PeerIndex id) =>
        Volatile.Write(ref emoteIds[(int)id.Value], null);
}