using Decentraland.Pulse;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Globalization;

namespace Pulse.Clusters;

/// <summary>One immutable operation still required before the wallet can receive room credentials.</summary>
public readonly record struct PendingRoomCleanup(string OperationId, string ClusterId, ulong MinimumRevokeBefore);

/// <summary>A complete immutable backend room plan and its captured live registration.</summary>
public sealed record RoomRecoveryAssignment(
    ClusterAssignment Assignment, PeerIndex? Peer, IdentityRegistration? Identity, string Epoch, string Revision,
    bool CleanupOnly, bool Ready, bool BootstrapRequired, ulong TokenNotBefore,
    IReadOnlyList<PendingRoomCleanup> Operations);

/// <summary>Operator-visible progress for this process's controlled room reset.</summary>
public readonly record struct RoomRecoveryStatus(string Epoch, bool BootstrapRequired, int PendingOperations, int RetainedWallets);

internal readonly record struct DesiredRoomAssignment(ClusterAssignment Assignment, PeerIndex Peer, IdentityRegistration Identity);

/// <summary>
///     Retains room obligations independently of notification delivery. Only the cluster tracker may mutate it.
/// </summary>
internal sealed class RoomRecoveryLedger
{
    // Covers up to five seconds of credential backdating and ten seconds between Pulse and Gatekeeper clocks.
    // Both service clocks must remain within five seconds of LiveKit Cloud for retirement pruning to be safe.
    internal const ulong RETIREMENT_CLOCK_GRACE_SECONDS = 15;

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly int maxWallets;
    private readonly int maxRooms;
    private ulong nextRevision;
    private ulong nextOperation;
    private ulong currentTime;
    private bool bootstrapRequired = true;

    internal RoomRecoveryLedger(ClusterOptions options, string? epoch = null)
    {
        Epoch = epoch ?? Guid.NewGuid().ToString("N");
        maxWallets = Math.Max(0, options.MaxRecoveryWallets);
        maxRooms = Math.Max(0, options.MaxRecoveryRoomsPerWallet);
    }

    internal string Epoch { get; }

    internal bool CapacityBlocked { get; private set; }

    internal void Reconcile(IReadOnlyDictionary<string, DesiredRoomAssignment> desired, ulong now)
    {
        currentTime = now;
        CapacityBlocked = false;
        foreach ((string wallet, Entry entry) in entries)
        {
            if (desired.ContainsKey(wallet)) continue;
            if (entry.Active)
            {
                entry.Active = false;
                entry.Peer = null;
                entry.Identity = null;
                entry.Revision = NewRevision();
                entry.NeedsRetirement = true;
                entry.RetirementObserved = false;
            }

            RetirePotentialRoom(entry, now);
            entry.NeedsRetirement = entry.PotentialRoom is not null;
            if (CanForget(entry)) entries.Remove(wallet);
        }

        foreach ((string wallet, DesiredRoomAssignment target) in desired)
        {
            if (!entries.TryGetValue(wallet, out Entry? entry))
            {
                if (entries.Count >= maxWallets)
                {
                    CapacityBlocked = true;
                    continue;
                }

                // Unique epoch-scoped rooms and the mandatory controlled reset establish the first baseline.
                entry = new Entry { Assignment = target.Assignment, Revision = NewRevision() };
                entries.Add(wallet, entry);
            }
            else if (!entry.Active || entry.Assignment != target.Assignment)
            {
                entry.Revision = NewRevision();
                entry.NeedsRetirement = true;
                entry.TargetFloor = 0;
                entry.RetirementObserved = false;
            }

            entry.Assignment = target.Assignment;
            entry.Active = true;
            entry.Peer = target.Peer;
            entry.Identity = target.Identity;
            entry.Blocked = false;
            if (entry.NeedsRetirement)
            {
                RetirePotentialRoom(entry, now);
                entry.NeedsRetirement = entry.PotentialRoom is not null;
            }

            if (entry.Blocked) CapacityBlocked = true;
        }
    }

    internal void Apply(RoomRecoveryConfirmation confirmation, ulong now)
    {
        currentTime = now;
        if (!string.Equals(confirmation.Epoch, Epoch, StringComparison.Ordinal)) return;
        if (confirmation.Bootstrap)
        {
            bootstrapRequired = false;
            return;
        }

        if (!entries.TryGetValue(confirmation.Wallet, out Entry? entry)
            || !string.Equals(entry.Revision, confirmation.Revision, StringComparison.Ordinal)) return;

        if (confirmation.ObservedReady)
        {
            if (!entry.Active && entry.Pending.Count == 0 && entry.PotentialRoom is null && !bootstrapRequired
                && !entry.NeedsRetirement && !entry.Blocked && confirmation.OperationId.Length == 0
                && confirmation.ClusterId.Length == 0 && confirmation.RevokeBefore == 0)
                entry.RetirementObserved = true;
            return;
        }

        if (!entry.Pending.TryGetValue(confirmation.ClusterId, out PendingRoomCleanup operation)
            || !string.Equals(operation.OperationId, confirmation.OperationId, StringComparison.Ordinal)
            || confirmation.RevokeBefore < operation.MinimumRevokeBefore
            || confirmation.RevokeBefore > now + 60)
            return;

        entry.Pending.Remove(confirmation.ClusterId);
        entry.CutoffFloor = Math.Max(entry.CutoffFloor, confirmation.RevokeBefore);
        if (entry.Active && string.Equals(entry.Assignment.ClusterId, confirmation.ClusterId, StringComparison.Ordinal))
            entry.TargetFloor = Math.Max(entry.TargetFloor, confirmation.RevokeBefore);
    }

    internal IReadOnlyDictionary<string, RoomRecoveryAssignment> Snapshot(out RoomRecoveryStatus status)
    {
        var snapshot = new Dictionary<string, RoomRecoveryAssignment>(entries.Count, StringComparer.Ordinal);
        var pending = 0;
        foreach ((string wallet, Entry entry) in entries)
        {
            if (CanForget(entry))
            {
                entries.Remove(wallet);
                continue;
            }

            bool ready = !bootstrapRequired && !entry.Blocked
                         && !entry.NeedsRetirement && entry.Pending.Count == 0;
            if (ready && entry.Active)
            {
                entry.PotentialRoom = entry.Assignment.ClusterId;
                entry.PotentialFloor = Math.Max(entry.TargetFloor, entry.CutoffFloor);
            }

            var operations = new PendingRoomCleanup[entry.Pending.Count];
            entry.Pending.Values.CopyTo(operations, 0);
            pending += operations.Length;
            snapshot.Add(wallet, new RoomRecoveryAssignment(entry.Assignment, entry.Peer, entry.Identity,
                Epoch, entry.Revision, !entry.Active, ready, bootstrapRequired, Math.Max(entry.TargetFloor, entry.CutoffFloor),
                Array.AsReadOnly(operations)));
        }

        status = new RoomRecoveryStatus(Epoch, bootstrapRequired, pending, entries.Count);
        return snapshot;
    }

    internal static RoomRecoveryPlan Message(RoomRecoveryAssignment assignment)
    {
        var plan = new RoomRecoveryPlan
        {
            Epoch = assignment.Epoch,
            Revision = assignment.Revision,
            Admission = assignment.Ready ? RoomAdmissionState.Ready : RoomAdmissionState.Pending,
            CleanupOnly = assignment.CleanupOnly,
            BootstrapRequired = assignment.BootstrapRequired,
            TokenNotBefore = assignment.TokenNotBefore,
        };
        foreach (PendingRoomCleanup operation in assignment.Operations)
            plan.Operations.Add(new RoomCleanupOperation
            {
                OperationId = operation.OperationId,
                ClusterId = operation.ClusterId,
                MinimumRevokeBefore = operation.MinimumRevokeBefore,
            });
        return plan;
    }

    private string NewRevision() => (++nextRevision).ToString(CultureInfo.InvariantCulture);

    private bool CanForget(Entry entry) => !entry.Active && entry.Pending.Count == 0
                                          && entry.PotentialRoom is null && entry.RetirementObserved
                                          && currentTime > entry.CutoffFloor + RETIREMENT_CLOCK_GRACE_SECONDS;

    private void RetirePotentialRoom(Entry entry, ulong now)
    {
        if (entry.PotentialRoom is not { } room) return;
        if (EnsureCleanup(entry, room, entry.PotentialFloor, now))
            entry.PotentialRoom = null;
    }

    private bool EnsureCleanup(Entry entry, string room, ulong floor, ulong now)
    {
        if (entry.Pending.ContainsKey(room)) return true;
        if (entry.Pending.Count >= maxRooms)
        {
            entry.Blocked = true;
            CapacityBlocked = true;
            return false;
        }

        entry.Pending.Add(room, new PendingRoomCleanup((++nextOperation).ToString(CultureInfo.InvariantCulture),
            room, Math.Max(now, Math.Max(floor, entry.CutoffFloor) + 1)));
        return true;
    }

    private sealed class Entry
    {
        public readonly Dictionary<string, PendingRoomCleanup> Pending = new(StringComparer.Ordinal);
        public ClusterAssignment Assignment;
        public PeerIndex? Peer;
        public IdentityRegistration? Identity;
        public string Revision = string.Empty;
        public bool Active;
        public bool NeedsRetirement;
        public bool RetirementObserved;
        public bool Blocked;
        // Ready is permission to mint, so this room remains potentially occupied even without delivery evidence.
        public string? PotentialRoom;
        public ulong PotentialFloor;
        public ulong TargetFloor;
        // A wallet-wide maximum bounds memory while preserving same-room floors after confirmed movement.
        public ulong CutoffFloor;
    }
}
