using Pulse.Peers.Simulation;

namespace Pulse.Peers;

public sealed class PeerOptions
{
    public const string SECTION_NAME = "Peers";

    public int SnapshotHistoryCapacity { get; set; } = 10;

    /// <summary>
    ///     Maximum number of worker threads. Defaults to <see cref="Environment.ProcessorCount" />.
    ///     Useful for limiting thread count on machines with many cores.
    /// </summary>
    public int MaxWorkerThreads { get; set; }

    /// <summary>
    ///     Simulation steps in milliseconds for tiers
    /// </summary>
    public uint[] SimulationSteps { get; set; } = new[] { 50u, 100u, 200u };

    /// <summary>
    ///     When enabled, each peer receives its own state updates as if from another peer
    ///     identified by <see cref="PeerSimulation.SELF_MIRROR_WALLET_ID" />.
    /// </summary>
    public bool SelfMirrorEnabled { get; set; }

    /// <summary>
    ///     Tier index (0, 1, 2) used for self-mirror updates, controlling update frequency and field detail.
    /// </summary>
    public int SelfMirrorTier { get; set; }

    /// <summary>
    ///     When enabled, resync responses attempt a targeted delta from the client's
    ///     known baseline before falling back to STATE_FULL.
    /// </summary>
    public bool ResyncWithDelta { get; set; }

    /// <summary>
    ///     How long a peer remains in DISCONNECTING before <c>CleanupDisconnectedPeer</c> fires,
    ///     wipes every per-peer board, and releases the slot back to
    ///     <see cref="PeerIndexAllocator" />. This is the single clock governing slot reuse — the
    ///     allocator has no independent timer, so its pending-recycle state and the simulation's
    ///     cleanup state cannot drift.
    ///     <para />
    ///     Must stay above the stale-view sweep interval (<c>SWEEP_INTERVAL × BaseTickMs</c>, ≈5 s)
    ///     so observers get a chance to emit <c>PlayerLeft</c> before the slot is reused. The
    ///     auth-timeout path funnels through the same sequence — a PENDING_AUTH peer that times
    ///     out is transport-disconnected, which triggers the ENet disconnect event and the same
    ///     DISCONNECTING → cleanup → release flow.
    /// </summary>
    public uint DisconnectionCleanTimeoutMs { get; set; } = 5000;

    /// <summary>
    ///     Maximum time a peer may stay in PENDING_AUTH before being force-disconnected. After
    ///     force-disconnect, the slot still follows the normal disconnect cleanup path governed
    ///     by <see cref="DisconnectionCleanTimeoutMs" />.
    /// </summary>
    public uint PendingAuthCleanTimeoutMs { get; set; } = 30000;
}
