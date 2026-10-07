using Pulse.InterestManagement;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using System.Numerics;

namespace DCLPulseTests;

[TestFixture]
public class InterestCollectorTests
{
    [Test]
    public void DuplicateSubject_PreservesFirstSequenceTierAndRegistration()
    {
        var collector = new InterestCollector();
        PeerIndex subject = new (1);
        PeerSnapshot first = TestSnapshots.Make(seq: 10, realm: "realm-a", position: new Vector3(1, 0, 1));
        PeerSnapshot later = TestSnapshots.Make(seq: 11, realm: "realm-b", position: new Vector3(50, 0, 50));
        var firstIdentity = new IdentityRegistration("wallet", "session", 1);
        var laterIdentity = new IdentityRegistration("wallet", "session", 2);

        collector.Add(subject, PeerViewSimulationTier.TIER_0, first.Seq, firstIdentity);
        collector.Add(subject, PeerViewSimulationTier.TIER_2, later.Seq, laterIdentity);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Subject, Is.EqualTo(subject));
        Assert.That(collector.Entries[0].Seq, Is.EqualTo(first.Seq));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_0));
        Assert.That(collector.Entries[0].Identity, Is.SameAs(firstIdentity));
    }

    [Test]
    public void Clear_AllowsSameSubjectWithNewSequenceAndRegistration()
    {
        var collector = new InterestCollector();
        PeerIndex subject = new (1);
        PeerSnapshot first = TestSnapshots.Make(seq: 10, realm: "realm-a");
        var firstIdentity = new IdentityRegistration("wallet", "session", 1);
        collector.Add(subject, PeerViewSimulationTier.TIER_0, first.Seq, firstIdentity);

        collector.Clear();
        Assert.That(collector.Count, Is.Zero);
        Assert.That(collector.Entries, Is.Empty);

        PeerSnapshot next = TestSnapshots.Make(seq: 1, realm: "realm-b");
        var nextIdentity = new IdentityRegistration("wallet", "session", 2);
        collector.Add(subject, PeerViewSimulationTier.TIER_1, next.Seq, nextIdentity);

        Assert.That(collector.Count, Is.EqualTo(1));
        Assert.That(collector.Entries[0].Seq, Is.EqualTo(next.Seq));
        Assert.That(collector.Entries[0].Tier, Is.EqualTo(PeerViewSimulationTier.TIER_1));
        Assert.That(collector.Entries[0].Identity, Is.SameAs(nextIdentity));
    }

    [Test]
    public void DistinctSubjectsWithSameWallet_AcceptedSeparately()
    {
        var collector = new InterestCollector();
        PeerSnapshot snapshot = TestSnapshots.Make(realm: "realm-a");
        collector.Add(new PeerIndex(1), PeerViewSimulationTier.TIER_0, snapshot.Seq,
            new IdentityRegistration("wallet", "session-a", 1));
        collector.Add(new PeerIndex(2), PeerViewSimulationTier.TIER_0, snapshot.Seq,
            new IdentityRegistration("wallet", "session-b", 2));

        Assert.That(collector.Entries.Select(entry => entry.Subject),
            Is.EquivalentTo(new[] { new PeerIndex(1), new PeerIndex(2) }));
    }

    [Test]
    public void SourceSnapshotMutation_DoesNotChangeAcceptedSequence()
    {
        var collector = new InterestCollector();
        PeerSnapshot snapshot = TestSnapshots.Make(seq: 10, realm: "realm-a", position: new Vector3(1, 0, 1));
        uint acceptedSeq = snapshot.Seq;
        collector.Add(new PeerIndex(1), PeerViewSimulationTier.TIER_0, snapshot.Seq,
            new IdentityRegistration("wallet", "session", 1));

        snapshot.Seq = 11;
        snapshot.Realm = "realm-b";
        snapshot.GlobalPosition = new Vector3(100, 0, 100);

        Assert.That(collector.Entries[0].Seq, Is.EqualTo(acceptedSeq));
    }
}
