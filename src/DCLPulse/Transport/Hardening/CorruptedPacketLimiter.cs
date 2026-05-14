using Microsoft.Extensions.Options;
using Pulse.Peers;

namespace Pulse.Transport.Hardening;

/// <summary>
///     Per-peer token bucket that tolerates a small rate of corrupt packets before terminating
///     the session. Counts oversized packets (caught in the ENet receive handler before
///     <c>CopyTo</c>), protobuf parse failures (caught in <c>MessagePipe.OnDataReceived</c>),
///     and Receive events that arrive on an ENet slot we don't recognise.
///     <para />
///     Two key spaces are tracked independently:
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="PeerIndex" /> — the logical, allocator-issued peer id. Used for
///                 packets from peers that have already passed Connect.
///             </description>
///         </item>
///         <item>
///             <description>
///                 ENet slot id (<c>uint</c>) — used when a Receive event fires for a slot that
///                 was never registered, so we have no <see cref="PeerIndex" /> to charge.
///             </description>
///         </item>
///     </list>
///     Threading: every call site runs on the single ENet thread, so both dictionaries are
///     plain (no locking). Release counterparts are invoked from the ENet thread's Disconnect
///     handler.
/// </summary>
public sealed class CorruptedPacketLimiter
{
    private readonly Dictionary<PeerIndex, Bucket> peerBuckets = new ();
    private readonly Dictionary<uint, Bucket> slotBuckets = new ();
    private readonly byte burstCapacity;
    private readonly uint refillIntervalMs;
    private readonly ITimeProvider timeProvider;

    public CorruptedPacketLimiter(IOptions<CorruptedPacketLimiterOptions> options, ITimeProvider timeProvider)
    {
        CorruptedPacketLimiterOptions o = options.Value;
        burstCapacity = (byte)Math.Clamp(o.BurstCapacity, 0, byte.MaxValue);
        refillIntervalMs = o.MaxPerMinute > 0 ? (uint)(60_000 / o.MaxPerMinute) : 0;
        this.timeProvider = timeProvider;
    }

    public bool IsEnabled => refillIntervalMs > 0 && burstCapacity > 0;

    /// <summary>
    ///     Debits one token for the peer's bucket. Returns <c>true</c> when the budget is
    ///     exhausted and the caller should disconnect the peer with
    ///     <see cref="DisconnectReason.PACKET_CORRUPTED" />.
    /// </summary>
    public bool RegisterAndCheckExhausted(PeerIndex peerIndex) =>
        DebitOne(peerBuckets, peerIndex);

    /// <summary>
    ///     Sibling of <see cref="RegisterAndCheckExhausted(PeerIndex)" /> for Receive events on
    ///     unknown ENet slots — same per-slot bucket semantics so a hostile peer can't escape
    ///     the limiter by hitting the lifecycle race window between Connect and the slot dict
    ///     update.
    /// </summary>
    public bool RegisterAndCheckExhaustedForSlot(uint slotId) =>
        DebitOne(slotBuckets, slotId);

    /// <summary>Drops the peer's bucket on disconnect to bound the dictionary. Idempotent.</summary>
    public void Release(PeerIndex peerIndex) => peerBuckets.Remove(peerIndex);

    /// <summary>Drops the slot's bucket on Disconnect/Timeout. Idempotent.</summary>
    public void ReleaseSlot(uint slotId) => slotBuckets.Remove(slotId);

    private bool DebitOne<TKey>(Dictionary<TKey, Bucket> map, TKey key) where TKey : notnull
    {
        if (!IsEnabled) return false;

        uint now = timeProvider.MonotonicTime;

        if (!map.TryGetValue(key, out Bucket bucket) || bucket.LastRefillMs == 0)
        {
            // First corruption for this key — start with a full bucket. Clamp 0 to 1 so the
            // sentinel doesn't collide with an actual MonotonicTime reading of 0.
            bucket = new Bucket(burstCapacity, now == 0 ? 1u : now);
        }
        else
        {
            uint refills = (now - bucket.LastRefillMs) / refillIntervalMs;

            if (refills > 0)
            {
                byte refilled = (byte)Math.Min(burstCapacity, bucket.Tokens + refills);
                // Preserve sub-interval remainder so the long-run rate stays accurate.
                bucket = new Bucket(refilled, bucket.LastRefillMs + (refills * refillIntervalMs));
            }
        }

        if (bucket.Tokens == 0)
        {
            map[key] = bucket;
            return true;
        }

        map[key] = new Bucket((byte)(bucket.Tokens - 1), bucket.LastRefillMs);
        return false;
    }

    private readonly record struct Bucket(byte Tokens, uint LastRefillMs);
}
