using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;

namespace DCLPulseTests.Hardening;

/// <summary>
///     Drives <see cref="BanEnforcer" /> with simulated gatekeeper snapshots — no HTTP call
///     is made. The sequence of <c>Apply</c> calls in each test stands in for successive
///     polls of the real endpoint.
/// </summary>
[TestFixture]
public class BanEnforcerTests
{
    private const int MAX_PEERS = 16;

    private BanList banList;
    private MessagePipe messagePipe;
    private IdentityBoard identityBoard;
    private BanEnforcer enforcer;

    [SetUp]
    public void SetUp()
    {
        banList = new BanList();
        messagePipe = new MessagePipe(
            Substitute.For<ILogger<MessagePipe>>(),
            new ServerMessageCounters(10));
        identityBoard = new IdentityBoard(MAX_PEERS);
        enforcer = new BanEnforcer(
            Substitute.For<ILogger<BanEnforcer>>(),
            banList,
            messagePipe,
            identityBoard);
    }

    [Test]
    public void NewlyBannedConnectedPeer_EnqueuesDisconnect()
    {
        var peer = new PeerIndex(3);
        identityBoard.Set(peer, "0xabc");

        enforcer.Apply(["0xabc"]);

        Assert.That(messagePipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg), Is.True);
        Assert.That(msg.To, Is.EqualTo(peer));
        Assert.That(msg.IsDisconnect, Is.True);
        Assert.That(msg.Disconnect, Is.EqualTo(DisconnectReason.BANNED));
    }

    [Test]
    public void BannedWalletNotConnected_PopulatesListWithoutEviction()
    {
        enforcer.Apply(["0xabc"]);

        Assert.That(messagePipe.OutgoingQueueDepth, Is.EqualTo(0),
            "No peer is registered for this wallet — no eviction should be attempted");
        Assert.That(banList.IsBanned("0xabc"), Is.True,
            "List must still be populated for future handshake-time enforcement");
    }

    [Test]
    public void AlreadyBannedWallet_NotEvictedOnSubsequentApply()
    {
        var peer = new PeerIndex(3);
        identityBoard.Set(peer, "0xabc");

        enforcer.Apply(["0xabc"]);
        messagePipe.TryReadOutgoingMessage(out _);

        // Simulated second poll returns the same wallet — already banned, must not re-evict.
        enforcer.Apply(["0xabc"]);

        Assert.That(messagePipe.OutgoingQueueDepth, Is.EqualTo(0),
            "A wallet that was already banned in the previous snapshot must not trigger a second disconnect");
    }

    [Test]
    public void CaseInsensitive_IdentityBoardLookup()
    {
        var peer = new PeerIndex(5);
        identityBoard.Set(peer, "0xABCDEF0000000000000000000000000000000000");

        enforcer.Apply(["0xabcdef0000000000000000000000000000000000"]);

        Assert.That(messagePipe.TryReadOutgoingMessage(out MessagePipe.OutgoingMessage msg), Is.True,
            "IdentityBoard lookup is case-insensitive — wallet casing mismatch must not stop eviction");
        Assert.That(msg.To, Is.EqualTo(peer));
    }

    [Test]
    public void Unban_IsSilent_NoReviveMessage()
    {
        var peer = new PeerIndex(3);
        identityBoard.Set(peer, "0xabc");

        enforcer.Apply(["0xabc"]);
        messagePipe.TryReadOutgoingMessage(out _);

        // Simulated subsequent poll: wallet removed upstream. No "unban" side effect required.
        enforcer.Apply([]);

        Assert.That(banList.IsBanned("0xabc"), Is.False);
        Assert.That(messagePipe.OutgoingQueueDepth, Is.EqualTo(0),
            "Unban must be silent — no outgoing message should be produced");
    }

    [Test]
    public void MultipleNewlyBanned_AllEvicted()
    {
        var peerA = new PeerIndex(2);
        var peerB = new PeerIndex(4);
        identityBoard.Set(peerA, "0xaaa");
        identityBoard.Set(peerB, "0xbbb");

        enforcer.Apply(["0xaaa", "0xbbb", "0xccc"]);

        Assert.That(messagePipe.OutgoingQueueDepth, Is.EqualTo(2),
            "Every connected peer whose wallet just appeared on the list must be evicted");
    }
}
