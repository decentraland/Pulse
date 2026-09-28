using Decentraland.Pulse;
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
        bool resolved = publisher.TryResolveAssignment($"peer.{WALLET}.cluster_assignment", Encoding.UTF8.GetBytes(session),
            out PeerClusterChange? response);
        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.True);
            Assert.That(response?.ClusterId, Is.EqualTo("C1"));
            Assert.That(response?.Realm, Is.EqualTo("realm-a"));
            Assert.That(response?.Session, Is.EqualTo(SESSION));
            Assert.That(response?.DisplacedSession, Is.Empty);
            Assert.That(response?.DisplacedClusterId, Is.Empty);
        });
    }

    [Test]
    public void Lookup_ChecksumCasedWalletInTheSubject_ResolvesTheLowerCasedKey()
    {
        publisher.TryResolveAssignment($"peer.{WALLET.ToUpperInvariant()}.cluster_assignment", Encoding.UTF8.GetBytes(SESSION),
            out PeerClusterChange? response);
        Assert.That(response?.ClusterId, Is.EqualTo("C1"));
    }

    [TestCase("")]
    [TestCase(OTHER_SESSION)]
    [TestCase("garbage")]
    [TestCase("0xgggggggggggggggggggggggggggggggggggggggg")]
    [TestCase(SESSION + " ")]
    [TestCase(SESSION + SESSION)]
    public void Lookup_MissingWrongOrMalformedSession_ResolvesNothing(string session)
    {
        Assert.That(Resolve($"peer.{WALLET}.cluster_assignment", session), Is.Null);
    }

    [TestCase("peer.0x2222222222222222222222222222222222222222.cluster_assignment")]
    [TestCase("peer.*.cluster_assignment")]
    [TestCase("peer.wallet.cluster_assignment")]
    [TestCase("peer.0x1111111111111111111111111111111111111111.cluster_change")]
    [TestCase("other.0x1111111111111111111111111111111111111111.cluster_assignment")]
    [TestCase("peer.0x1111111111111111111111111111111111111111.cluster_assignment.extra")]
    public void Lookup_UnknownWalletOrInvalidSubject_ResolvesNothing(string subject)
    {
        Assert.That(Resolve(subject, SESSION), Is.Null);
    }

    [Test]
    public void Lookup_AfterDeparture_DoesNotReturnRetainedAssignment()
    {
        board.PublishAssignments(new Dictionary<string, ClusterAssignment>());
        Assert.That(Resolve($"peer.{WALLET}.cluster_assignment", SESSION), Is.Null);
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
            Assert.That(Resolve($"peer.{WALLET}.cluster_assignment", SESSION), Is.Null);
            Assert.That(Resolve($"peer.{WALLET}.cluster_assignment", OTHER_SESSION)?.ClusterId, Is.EqualTo("C2"));
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

    private PeerClusterChange? Resolve(string subject, string body) =>
        publisher.TryResolveAssignment(subject, Encoding.UTF8.GetBytes(body), out PeerClusterChange? response) ? response : null;
}
