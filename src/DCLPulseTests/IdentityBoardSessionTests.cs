using Pulse.Peers;
using Pulse.Peers.Simulation;

namespace DCLPulseTests;

[TestFixture]
public class IdentityBoardSessionTests
{
    private const int MAX_PEERS = 8;

    private IdentityBoard board;

    [SetUp]
    public void SetUp() =>
        board = new IdentityBoard(MAX_PEERS);

    [Test]
    public void Set_WithSession_ExposesItByPeer()
    {
        board.Set(new PeerIndex(1), "0xwallet", "0xsession");

        Assert.That(board.GetSessionByPeerIndex(new PeerIndex(1)), Is.EqualTo("0xsession"));
        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(1)), Is.EqualTo("0xwallet"));
    }

    [Test]
    public void Set_WithoutSession_UsesTheWalletAsTheSession()
    {
        board.Set(new PeerIndex(1), "0xwallet");

        Assert.That(board.GetSessionByPeerIndex(new PeerIndex(1)), Is.EqualTo("0xwallet"));
    }

    [Test]
    public void Remove_ClearsTheSessionWithTheWallet()
    {
        board.Set(new PeerIndex(1), "0xwallet", "0xsession");

        board.Remove(new PeerIndex(1));

        Assert.That(board.GetSessionByPeerIndex(new PeerIndex(1)), Is.Null);
        Assert.That(board.GetWalletIdByPeerIndex(new PeerIndex(1)), Is.Null);
    }

    [Test]
    public void Set_RebindingTheWalletToAnotherPeer_KeepsTheOldPeersSessionUntilItIsRemoved()
    {
        board.Set(new PeerIndex(1), "0xwallet", "0xsession-a");
        board.Set(new PeerIndex(2), "0xwallet", "0xsession-b");

        Assert.That(board.GetSessionByPeerIndex(new PeerIndex(1)), Is.EqualTo("0xsession-a"));
        Assert.That(board.GetSessionByPeerIndex(new PeerIndex(2)), Is.EqualTo("0xsession-b"));
        Assert.That(board.TryGetPeerIndexByWallet("0xwallet", out PeerIndex live), Is.True);
        Assert.That(live, Is.EqualTo(new PeerIndex(2)));
    }
}
