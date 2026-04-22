namespace Pulse.Peers;

/// <summary>
///     Shared convention for mapping a <see cref="PeerIndex" /> to a worker shard. Used by
///     <see cref="PeersManager" /> to route incoming events and by the handshake flow to
///     decide whether a cross-session reclamation would land on the same worker — cross-worker
///     rekey needs cleanup cancellation that the current design does not provide.
/// </summary>
public static class WorkerShard
{
    public static int ComputeWorkerCount(int maxWorkerThreads)
    {
        int processorCount = Environment.ProcessorCount;

        return maxWorkerThreads > 0
            ? Math.Min(maxWorkerThreads, processorCount)
            : processorCount;
    }

    public static int For(PeerIndex peerIndex, int workerCount) =>
        (int)(peerIndex.Value % (uint)workerCount);
}
