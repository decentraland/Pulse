using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pulse.Clusters;
using Pulse.Peers.Simulation;
using System.Text;

namespace DCLPulseTests;

[TestFixture]
public class ClusterAssignmentRecoveryTests
{
    private const string WALLET = "0x1111111111111111111111111111111111111111";
    private const string SESSION = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OTHER_SESSION = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private ClusterBoard board;
    private NatsPublisher publisher;

    [SetUp]
    public void SetUp()
    {
        board = new ClusterBoard();
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>
        {
            [WALLET] = new ("C1", "realm-a", SESSION),
        });
        publisher = new NatsPublisher(NullLogger<NatsPublisher>.Instance, NullLoggerFactory.Instance,
            Options.Create(new NatsOptions()), new SnapshotBoard(10, 4), board);
    }

    [TearDown]
    public void TearDown() => publisher.Dispose();

    [TestCase(SESSION)]
    [TestCase("0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Lookup_ActiveSession_ReturnsCurrentAssignment(string session)
    {
        var response = publisher.ResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(session));
        Assert.Multiple(() =>
        {
            Assert.That(response.ClusterId, Is.EqualTo("C1"));
            Assert.That(response.Realm, Is.EqualTo("realm-a"));
            Assert.That(response.Session, Is.EqualTo(SESSION));
            Assert.That(response.DisplacedSession, Is.Empty);
            Assert.That(response.DisplacedClusterId, Is.Empty);
        });
    }

    [Test]
    public void Lookup_ChecksumCasedWalletInTheSubject_ResolvesTheLowerCasedKey()
    {
        var response = publisher.ResolveAssignment($"peer.{WALLET.ToUpperInvariant()}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION));
        Assert.That(response.ClusterId, Is.EqualTo("C1"));
    }

    [TestCase("")]
    [TestCase(OTHER_SESSION)]
    [TestCase("garbage")]
    [TestCase("0xgggggggggggggggggggggggggggggggggggggggg")]
    public void Lookup_MissingWrongOrMalformedSession_ReturnsEmpty(string session)
    {
        var response = publisher.ResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(session));
        Assert.That(response.CalculateSize(), Is.Zero);
    }

    [TestCase("peer.0x2222222222222222222222222222222222222222.cluster_assignment")]
    [TestCase("peer.*.cluster_assignment")]
    [TestCase("peer.wallet.cluster_assignment")]
    [TestCase("peer.0x1111111111111111111111111111111111111111.cluster_change")]
    [TestCase("other.0x1111111111111111111111111111111111111111.cluster_assignment")]
    public void Lookup_UnknownWalletOrInvalidSubject_ReturnsEmpty(string subject)
    {
        Assert.That(publisher.ResolveAssignment(subject, Encoding.UTF8.GetBytes(SESSION)).CalculateSize(), Is.Zero);
    }

    [Test]
    public void Lookup_AfterDeparture_DoesNotReturnRetainedAssignment()
    {
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>());
        Assert.That(publisher.ResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION)).CalculateSize(), Is.Zero);
    }

    [Test]
    public void Lookup_AfterTakeover_RejectsOldSessionAndReturnsNewRoom()
    {
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>
        {
            [WALLET] = new ("C2", "realm-b", OTHER_SESSION),
        });
        Assert.Multiple(() =>
        {
            Assert.That(publisher.ResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION)).CalculateSize(), Is.Zero);
            Assert.That(publisher.ResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(OTHER_SESSION)).ClusterId, Is.EqualTo("C2"));
        });
    }

    [TestCase("_INBOX.rXsK2Lm9Qw.1", true)]
    [TestCase("_INBOX.", true)]
    [TestCase("_inbox.rXsK2Lm9Qw.1", false)]
    [TestCase("peer.0x2222222222222222222222222222222222222222.cluster_change", false)]
    [TestCase("engine.islands", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsRequestInbox_AcceptsOnlyTheClientInboxPrefix(string? replyTo, bool expected)
    {
        Assert.That(NatsPublisher.IsRequestInbox(replyTo), Is.EqualTo(expected));
    }
}
