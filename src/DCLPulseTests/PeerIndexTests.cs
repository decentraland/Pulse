using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests;

/// <summary>
///     Pins the value-type contract of <see cref="PeerIndex" />.
///     These tests are the safety net for the upcoming refactor that decouples the logical
///     <see cref="PeerIndex" /> from the ENet transport slot id (<c>ENetPeer.ID</c>). The
///     refactor will change how <see cref="PeerIndex" /> values are allocated and recycled,
///     but must not change how the struct behaves as a dictionary key, how it serializes to
///     the wire as <c>uint</c>, or how two instances compare for equality.
/// </summary>
[TestFixture]
public class PeerIndexTests
{
    [Test]
    public void Equals_TwoInstancesWithSameValue_AreEqual()
    {
        var a = new PeerIndex(42);
        var b = new PeerIndex(42);

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a == b, Is.True);
        Assert.That(a != b, Is.False);
        Assert.That(a.Equals((object)b), Is.True);
    }

    [Test]
    public void Equals_TwoInstancesWithDifferentValues_AreNotEqual()
    {
        var a = new PeerIndex(42);
        var b = new PeerIndex(43);

        Assert.That(a.Equals(b), Is.False);
        Assert.That(a == b, Is.False);
        Assert.That(a != b, Is.True);
    }

    [Test]
    public void GetHashCode_MatchesValue()
    {
        Assert.That(new PeerIndex(0).GetHashCode(), Is.EqualTo(0));
        Assert.That(new PeerIndex(42).GetHashCode(), Is.EqualTo(42));
        Assert.That(new PeerIndex(uint.MaxValue).GetHashCode(), Is.EqualTo(unchecked((int)uint.MaxValue)));
    }

    [Test]
    public void GetHashCode_IsStableAcrossEqualInstances()
    {
        var a = new PeerIndex(42);
        var b = new PeerIndex(42);

        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void ImplicitConversionToUint_ReturnsRawValue()
    {
        uint v = new PeerIndex(7);

        Assert.That(v, Is.EqualTo(7u));
    }

    [Test]
    public void RoundTrip_UintToPeerIndexToUint_IsLossless()
    {
        uint[] values = [0, 1, 99, 4094, uint.MaxValue];

        foreach (uint original in values)
        {
            uint roundTripped = new PeerIndex(original);
            Assert.That(roundTripped, Is.EqualTo(original));
        }
    }

    [Test]
    public void WorksAsDictionaryKey_DistinctValuesDoNotCollide()
    {
        var dict = new Dictionary<PeerIndex, string>
        {
            [new PeerIndex(0)] = "zero",
            [new PeerIndex(1)] = "one",
            [new PeerIndex(4094)] = "max",
        };

        Assert.That(dict[new PeerIndex(0)], Is.EqualTo("zero"));
        Assert.That(dict[new PeerIndex(1)], Is.EqualTo("one"));
        Assert.That(dict[new PeerIndex(4094)], Is.EqualTo("max"));
        Assert.That(dict.ContainsKey(new PeerIndex(2)), Is.False);
    }

    [Test]
    public void WorksAsHashSetMember_DuplicateAddIsRejected()
    {
        var set = new HashSet<PeerIndex>();

        Assert.That(set.Add(new PeerIndex(5)), Is.True);
        Assert.That(set.Add(new PeerIndex(5)), Is.False, "inserting a PeerIndex with the same value should be a no-op");
        Assert.That(set.Contains(new PeerIndex(5)), Is.True);
    }

    [Test]
    public void ToString_ReturnsNumericRepresentation()
    {
        Assert.That(new PeerIndex(0).ToString(), Is.EqualTo("0"));
        Assert.That(new PeerIndex(42).ToString(), Is.EqualTo("42"));
    }

    [Test]
    public void ZeroIsAValidPeerIndex_AndDistinctFromDefault()
    {
        // `default(PeerIndex)` also has Value == 0; today that's indistinguishable from an
        // explicitly constructed PeerIndex(0). The allocator refactor should preserve this
        // — any code that uses `default(PeerIndex)` as a sentinel is relying on an out-of-band
        // convention, not on PeerIndex itself.
        var zero = new PeerIndex(0);
        PeerIndex @default = default;

        Assert.That(zero, Is.EqualTo(@default));
        Assert.That(zero.Value, Is.EqualTo(0u));
    }

    [Test]
    public void DefaultTransport_IsEnet()
    {
        // The single-arg constructor and default(PeerIndex) both stamp ENet (value 0), so untagged
        // indexes route to the ENet channel — this is what keeps the legacy/test path unchanged.
        Assert.That(new PeerIndex(5).Transport, Is.EqualTo(TransportId.ENet));
        Assert.That(default(PeerIndex).Transport, Is.EqualTo(TransportId.ENet));
    }

    [Test]
    public void TransportStamp_IsAppliedButDoesNotAffectIdentity()
    {
        var enet = new PeerIndex(5);
        var wt = new PeerIndex(5, TransportId.WebTransport);

        Assert.That(wt.Transport, Is.EqualTo(TransportId.WebTransport), "the stamp is applied");
        Assert.That(wt.Value, Is.EqualTo(5u), "the logical slot is unchanged");

        // The transport is a routing tag, NOT part of identity: equality, hashcode and the uint
        // conversion must ignore it, or dictionary keys and worker sharding would break.
        Assert.That(wt, Is.EqualTo(enet));
        Assert.That(wt == enet, Is.True);
        Assert.That(wt.GetHashCode(), Is.EqualTo(enet.GetHashCode()));
        Assert.That((uint)wt, Is.EqualTo((uint)enet));
    }

    [Test]
    public void TransportStamp_DoesNotAffectDictionaryLookup()
    {
        var dict = new Dictionary<PeerIndex, string> { [new PeerIndex(9, TransportId.ENet)] = "peer" };

        Assert.That(dict.ContainsKey(new PeerIndex(9, TransportId.WebTransport)), Is.True,
            "lookup is by value; the transport stamp must not change the key");
        Assert.That(dict[new PeerIndex(9)], Is.EqualTo("peer"));
    }
}
