using Pulse.Peers;

namespace DCLPulseTests;

[TestFixture]
public class PeerIndexAllocatorTests
{
    private const int MAX_PEERS = 8;

    private PeerIndexAllocator allocator;

    [SetUp]
    public void SetUp()
    {
        allocator = new PeerIndexAllocator(MAX_PEERS);
    }

    // ── Allocation ──────────────────────────────────────────────────────

    [Test]
    public void TryAllocate_OnFreshPool_ReturnsDistinctPeerIndexes()
    {
        var seen = new HashSet<PeerIndex>();

        for (var i = 0; i < MAX_PEERS; i++)
        {
            Assert.That(allocator.TryAllocate(out PeerIndex pi), Is.True);
            Assert.That(seen.Add(pi), Is.True, $"allocation #{i} returned a duplicate PeerIndex {pi}");
        }

        Assert.That(seen, Has.Count.EqualTo(MAX_PEERS));
    }

    [Test]
    public void TryAllocate_AllValuesAreInRange()
    {
        for (var i = 0; i < MAX_PEERS; i++)
        {
            Assert.That(allocator.TryAllocate(out PeerIndex pi), Is.True);
            Assert.That(pi.Value, Is.LessThan((uint)MAX_PEERS),
                "allocator must not issue a PeerIndex beyond maxPeers — array-backed boards would OOB");
        }
    }

    [Test]
    public void TryAllocate_WhenPoolExhausted_ReturnsFalse()
    {
        for (var i = 0; i < MAX_PEERS; i++)
            Assert.That(allocator.TryAllocate(out _), Is.True);

        Assert.That(allocator.TryAllocate(out _), Is.False);
    }

    // ── Pending / Release lockstep ──────────────────────────────────────

    [Test]
    public void MarkPending_PreventsReissue()
    {
        // Exhaust the pool; the only way to reissue a slot is for it to be Released.
        var slots = new List<PeerIndex>();

        for (var i = 0; i < MAX_PEERS; i++)
        {
            allocator.TryAllocate(out PeerIndex pi);
            slots.Add(pi);
        }

        allocator.MarkPending(slots[0]);

        Assert.That(allocator.TryAllocate(out _), Is.False,
            "MarkPending must not put the slot back in the free-list — it stays parked until Release");
        Assert.That(allocator.PendingCount, Is.EqualTo(1));
    }

    [Test]
    public void Release_AfterMarkPending_ReturnsSlotToFreeList()
    {
        var slots = new List<PeerIndex>();

        for (var i = 0; i < MAX_PEERS; i++)
        {
            allocator.TryAllocate(out PeerIndex pi);
            slots.Add(pi);
        }

        allocator.MarkPending(slots[0]);
        allocator.Release(slots[0]);

        Assert.That(allocator.TryAllocate(out PeerIndex reissued), Is.True);
        Assert.That(reissued, Is.EqualTo(slots[0]),
            "Release must return the pending slot to the free-list so it can be reissued");
        Assert.That(allocator.PendingCount, Is.EqualTo(0));
    }

    [Test]
    public void Release_WithoutPriorMarkPending_IsNoOp()
    {
        // The slot was allocated and never Marked. Release must not double-enqueue it.
        allocator.TryAllocate(out PeerIndex pi);

        allocator.Release(pi);
        allocator.Release(pi);

        // Pool is still down one slot from the initial allocation — Release did not resurrect it.
        int freeBefore = allocator.FreeCount;

        // Allocate everything that's free; none of them should equal `pi`.
        var allocated = new HashSet<PeerIndex>();
        while (allocator.TryAllocate(out PeerIndex next)) allocated.Add(next);

        Assert.That(allocated, Does.Not.Contain(pi),
            "Release without prior MarkPending must not return the slot to the free-list");
        Assert.That(allocated, Has.Count.EqualTo(freeBefore));
    }

    [Test]
    public void MarkPending_Idempotent()
    {
        allocator.TryAllocate(out PeerIndex pi);

        allocator.MarkPending(pi);
        allocator.MarkPending(pi);

        Assert.That(allocator.PendingCount, Is.EqualTo(1),
            "MarkPending twice for the same slot must not double-count");

        allocator.Release(pi);
        Assert.That(allocator.PendingCount, Is.EqualTo(0));
        Assert.That(allocator.FreeCount, Is.EqualTo(MAX_PEERS - 1 + 1),
            "Release after idempotent MarkPending must still enqueue exactly once");
    }

    [Test]
    public void Release_WhileOthersAreStillPending_DoesNotAffectThem()
    {
        var slots = new List<PeerIndex>();
        for (var i = 0; i < MAX_PEERS; i++)
        {
            allocator.TryAllocate(out PeerIndex pi);
            slots.Add(pi);
        }

        allocator.MarkPending(slots[0]);
        allocator.MarkPending(slots[1]);
        allocator.MarkPending(slots[2]);

        allocator.Release(slots[1]);

        Assert.That(allocator.PendingCount, Is.EqualTo(2),
            "releasing one pending slot must not free the others");

        // Only the released slot is reissuable.
        Assert.That(allocator.TryAllocate(out PeerIndex reissued), Is.True);
        Assert.That(reissued, Is.EqualTo(slots[1]));
        Assert.That(allocator.TryAllocate(out _), Is.False,
            "remaining pending slots must stay parked until their own Release");
    }
}
