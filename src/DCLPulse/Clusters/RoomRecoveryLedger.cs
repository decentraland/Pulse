using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace Pulse.Clusters;

/// <summary>One retained displacement, identified independently of its room or current assignment.</summary>
public readonly record struct PendingSessionTakeover(string TakeoverId, string DisplacedSession, string DisplacedClusterId);

/// <summary>An immutable assignment, pending takeovers and captured live registration.</summary>
public sealed record RoomRecoveryAssignment(
    ClusterAssignment Assignment, PeerIndex? Peer, IdentityRegistration? Identity,
    bool Active, bool CapacityBlocked, ulong TokenNotBefore, IReadOnlyList<PendingSessionTakeover> PendingTakeovers);

/// <summary>Diagnostic counts for retained takeover recovery.</summary>
public readonly record struct RoomRecoveryStatus(string BootId, int PendingOperations, int RetainedWallets, bool CapacityBlocked);

internal readonly record struct DesiredRoomAssignment(ClusterAssignment Assignment, PeerIndex Peer, IdentityRegistration Identity);

/// <summary>Retains unfinished takeovers independently of notification delivery, with one tracker writer.</summary>
internal sealed class RoomRecoveryLedger
{
    internal const ulong MAX_SAFE_CUTOFF = 9007199254740991;
    internal const ulong RETIREMENT_CLOCK_GRACE_SECONDS = 15;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly int maxWallets;
    private readonly int maxTakeovers;
    private readonly ulong retentionSeconds;
    private ulong currentTime;
    private IReadOnlyDictionary<string, DesiredRoomAssignment> currentDesired = new Dictionary<string, DesiredRoomAssignment>();

    internal RoomRecoveryLedger(ClusterOptions options, string? bootId = null)
    {
        BootId = bootId ?? Guid.NewGuid().ToString("N");
        maxWallets = Math.Max(0, options.MaxRecoveryWallets);
        maxTakeovers = Math.Max(0, options.MaxRecoveryRoomsPerWallet);
        retentionSeconds = (ulong)Math.Max(0, (long)options.SessionRetentionPasses * options.PassIntervalMs / 1000);
    }

    internal string BootId { get; }
    internal bool CapacityBlocked { get; private set; }

    internal void Reconcile(IReadOnlyDictionary<string, DesiredRoomAssignment> desired, ulong now)
    {
        currentTime = now;
        currentDesired = desired;
        foreach ((string wallet, Entry entry) in entries)
        {
            if (desired.ContainsKey(wallet)) continue;
            entry.Active = false;
            entry.Peer = null;
            entry.Identity = null;
            if (CanForget(entry)) entries.Remove(wallet);
        }

        foreach ((string wallet, DesiredRoomAssignment target) in desired)
        {
            if (!entries.TryGetValue(wallet, out Entry? entry))
            {
                if (entries.Count >= maxWallets) continue;
                entry = new Entry { Assignment = target.Assignment };
                entries.Add(wallet, entry);
            }
            if (entry.PotentialAssignment is { } exposed
                && !string.Equals(exposed.Session, target.Assignment.Session, StringComparison.OrdinalIgnoreCase)
                && entry.Pending.Count < maxTakeovers)
            {
                var pending = new PendingSessionTakeover(Guid.NewGuid().ToString("N"), exposed.Session, exposed.ClusterId);
                entry.Pending.Add(pending.TakeoverId, pending);
                entry.PendingFloors.Add(pending.TakeoverId, entry.TokenNotBefore);
                entry.PotentialAssignment = null;
            }
            entry.Assignment = target.Assignment;
            entry.Active = true;
            entry.Peer = target.Peer;
            entry.Identity = target.Identity;
            entry.LastSeen = now;
        }
        UpdateCapacity();
    }

    internal void Apply(RoomRecoveryConfirmation confirmation, ulong now)
    {
        currentTime = now;
        if (!entries.TryGetValue(confirmation.Wallet, out Entry? entry)
            || !entry.PendingFloors.TryGetValue(confirmation.TakeoverId, out ulong previousFloor)
            || confirmation.RevokeBefore == 0 || confirmation.RevokeBefore > MAX_SAFE_CUTOFF
            || confirmation.RevokeBefore <= previousFloor) return;
        entry.Pending.Remove(confirmation.TakeoverId);
        entry.PendingFloors.Remove(confirmation.TakeoverId);
        entry.TokenNotBefore = Math.Max(entry.TokenNotBefore, confirmation.RevokeBefore);
        UpdateCapacity();
    }

    internal IReadOnlyDictionary<string, RoomRecoveryAssignment> Snapshot(out RoomRecoveryStatus status)
    {
        var snapshot = new Dictionary<string, RoomRecoveryAssignment>(entries.Count, StringComparer.Ordinal);
        var pendingCount = 0;
        foreach ((string wallet, Entry entry) in entries)
        {
            bool blocked = IsBlocked(entry);
            if (entry.Active && !blocked && entry.Pending.Count == 0) entry.PotentialAssignment = entry.Assignment;
            var pending = new PendingSessionTakeover[entry.Pending.Count];
            entry.Pending.Values.CopyTo(pending, 0);
            pendingCount += pending.Length;
            snapshot.Add(wallet, new RoomRecoveryAssignment(entry.Assignment, entry.Peer, entry.Identity,
                entry.Active, blocked,
                entry.TokenNotBefore, Array.AsReadOnly(pending)));
        }
        foreach ((string wallet, DesiredRoomAssignment desired) in currentDesired)
            if (!snapshot.ContainsKey(wallet))
                snapshot.Add(wallet, new RoomRecoveryAssignment(desired.Assignment, desired.Peer, desired.Identity,
                    true, true, 0, Array.Empty<PendingSessionTakeover>()));
        status = new RoomRecoveryStatus(BootId, pendingCount, entries.Count, CapacityBlocked);
        return snapshot;
    }

    internal static void Stamp(PeerClusterChange message, RoomRecoveryAssignment? assignment)
    {
        message.PendingTakeovers.Clear();
        message.ClearTokenNotBefore();
        if (assignment is null) return;
        if (!assignment.CapacityBlocked) message.TokenNotBefore = assignment.TokenNotBefore;
        foreach (PendingSessionTakeover pending in assignment.PendingTakeovers)
            message.PendingTakeovers.Add(new PendingTakeover
            {
                TakeoverId = pending.TakeoverId,
                DisplacedSession = pending.DisplacedSession,
                DisplacedClusterId = pending.DisplacedClusterId,
            });
    }

    private bool CanForget(Entry entry) => entry.Pending.Count == 0 && !IsBlocked(entry)
        && currentTime > entry.LastSeen + retentionSeconds
        && currentTime > entry.TokenNotBefore + RETIREMENT_CLOCK_GRACE_SECONDS;

    private static bool IsBlocked(Entry entry) => entry.PotentialAssignment is { } exposed
        && !string.Equals(exposed.Session, entry.Assignment.Session, StringComparison.OrdinalIgnoreCase);

    private void UpdateCapacity()
    {
        CapacityBlocked = false;
        foreach (string wallet in currentDesired.Keys)
            if (!entries.ContainsKey(wallet)) CapacityBlocked = true;
        foreach (Entry entry in entries.Values)
            if (IsBlocked(entry)) CapacityBlocked = true;
    }
    private sealed class Entry
    {
        public readonly Dictionary<string, PendingSessionTakeover> Pending = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ulong> PendingFloors = new(StringComparer.Ordinal);
        public ClusterAssignment? PotentialAssignment;
        public ClusterAssignment Assignment;
        public PeerIndex? Peer;
        public IdentityRegistration? Identity;
        public bool Active;
        public ulong LastSeen;
        public ulong TokenNotBefore;
    }
}
