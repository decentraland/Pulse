using Decentraland.Pulse;

namespace Pulse.Peers.Simulation;

/// <summary>
///     Finalizes one-shot emotes whose duration has elapsed by publishing a stop snapshot
///     with <see cref="EmoteStopReason.Completed" />. Runs on the subject's worker loop so
///     every expired emote gets its own fresh sequence number in the ring — downstream
///     observer simulation picks it up through the normal intermediate-snapshot scan,
///     instead of the observer having to infer expiry lazily from the latest snapshot.
/// </summary>
public sealed class EmoteCompleter(SnapshotBoard snapshotBoard, ITimeProvider timeProvider)
{
    /// <summary>
    ///     Scans authenticated peers owned by the caller and publishes a <see cref="EmoteStopReason.Completed" />
    ///     stop snapshot for any one-shot emote whose duration has elapsed. Must be called on the worker
    ///     that owns <paramref name="peers" /> — single writer per peer's snapshot ring.
    /// </summary>
    public void CompleteExpiredEmotes(Dictionary<PeerIndex, PeerState> peers)
    {
        uint now = timeProvider.MonotonicTime;

        foreach ((PeerIndex id, PeerState state) in peers)
        {
            if (state.ConnectionState != PeerConnectionState.AUTHENTICATED)
                continue;

            // The ledger in SnapshotBoard.Publish carries the active emote forward onto every
            // snapshot, so the latest snapshot is authoritative for the current emote — safe
            // even when the original EmoteStart slot has been overwritten by a ring wrap.
            if (!snapshotBoard.TryRead(id, out PeerSnapshot current))
                continue;

            if (current.Emote is not { EmoteId: not null, DurationMs: { } durationMs } emote)
                continue;

            if (now < emote.StartTick || now - emote.StartTick < durationMs)
                continue;

            PeerSnapshot stop = current with
            {
                Seq = snapshotBoard.LastSeq(id) + 1,
                ServerTick = now,
                Emote = new EmoteState(null, StartSeq: emote.StartSeq, StartTick: emote.StartTick,
                    StopReason: EmoteStopReason.Completed),
            };

            snapshotBoard.Publish(id, in stop);
        }
    }
}
