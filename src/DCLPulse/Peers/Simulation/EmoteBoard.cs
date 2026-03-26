using Decentraland.Pulse;

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
    private readonly EmoteState?[] states = new EmoteState?[maxPeers];

    public void Start(PeerIndex id, string emoteId, uint serverTick, uint? durationMs) =>
        Volatile.Write(ref states[(int)id.Value], new EmoteState(emoteId, serverTick, null, durationMs));

    public void Stop(PeerIndex id, uint serverTick) =>
        // Maybe it could be useful to keep the start tick if its needed somehow in the future
        Volatile.Write(ref states[(int)id.Value], new EmoteState(null, 0, serverTick, StopReason: EmoteStopReason.Cancelled));

    public EmoteState? Get(PeerIndex id) =>
        Volatile.Read(ref states[(int)id.Value]);

    public bool IsEmoting(PeerIndex id) =>
        Volatile.Read(ref states[(int)id.Value])?.EmoteId != null;

    public void TryComplete(PeerIndex id, uint now)
    {
        EmoteState? state = Volatile.Read(ref states[(int)id.Value]);

        if (state?.EmoteId == null || state.DurationMs == null)
            return;

        if (now - state.StartTick >= state.DurationMs.Value)
            Volatile.Write(ref states[(int)id.Value], new EmoteState(null, 0, now, StopReason: EmoteStopReason.Completed));
    }

    public void Remove(PeerIndex id) =>
        Volatile.Write(ref states[(int)id.Value], null);
}

public record EmoteState(string? EmoteId, uint StartTick, uint? StopTick, uint? DurationMs = null, EmoteStopReason? StopReason = null);
