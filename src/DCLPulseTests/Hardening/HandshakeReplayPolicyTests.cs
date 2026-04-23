using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class HandshakeReplayPolicyTests
{
    private const string WALLET = "0xabc";
    private const string TS = "1700000000000";
    private static readonly PeerIndex PEER = new (1);

    private ITimeProvider timeProvider;
    private ITransport transport;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        timeProvider.MonotonicTime.Returns(1000u);
        transport = Substitute.For<ITransport>();
    }

    private HandshakeReplayPolicy Create(bool enabled = true, uint pendingAuthTtlMs = 30_000, int maxPeers = 4096) =>
        new (Options.Create(new HandshakeReplayPolicyOptions { Enabled = enabled }),
             new PeerOptions { PendingAuthCleanTimeoutMs = pendingAuthTtlMs },
             Options.Create(new ENetTransportOptions { MaxPeers = maxPeers }),
             timeProvider, transport);

    private static PeerState PendingPeer() =>
        new (PeerConnectionState.PENDING_AUTH);

    [Test]
    public void FirstAdmit_Succeeds()
    {
        HandshakeReplayPolicy policy = Create();

        Assert.That(policy.TryAdmit(PEER, PendingPeer(), WALLET, TS), Is.True);
        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void SamePairWithinWindow_RejectedAndDisconnects()
    {
        HandshakeReplayPolicy policy = Create();
        policy.TryAdmit(PEER, PendingPeer(), WALLET, TS);

        PeerState second = PendingPeer();
        Assert.That(policy.TryAdmit(PEER, second, WALLET, TS), Is.False);

        transport.Received(1).Disconnect(PEER, DisconnectReason.HANDSHAKE_REPLAY_REJECTED);
        Assert.That(second.ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
    }

    [Test]
    public void SamePairAfterTtl_Accepted()
    {
        HandshakeReplayPolicy policy = Create(pendingAuthTtlMs: 30_000);
        timeProvider.MonotonicTime.Returns(1000u);
        policy.TryAdmit(PEER, PendingPeer(), WALLET, TS);

        // Advance past TTL (matches PendingAuthCleanTimeoutMs).
        timeProvider.MonotonicTime.Returns(1000u + 30_001u);

        Assert.That(policy.TryAdmit(PEER, PendingPeer(), WALLET, TS), Is.True,
            "Same pair after TTL expires must be admitted — the PENDING_AUTH window has closed, "
          + "so the cache can forget it");
    }

    [Test]
    public void DifferentTimestamp_SameWallet_Accepted()
    {
        HandshakeReplayPolicy policy = Create();
        policy.TryAdmit(PEER, PendingPeer(), WALLET, TS);

        Assert.That(policy.TryAdmit(PEER, PendingPeer(), WALLET, "1700000000001"), Is.True,
            "Legitimate reconnect with a fresh timestamp must be admitted");
    }

    [Test]
    public void DifferentWallet_SameTimestamp_Accepted()
    {
        HandshakeReplayPolicy policy = Create();
        policy.TryAdmit(PEER, PendingPeer(), WALLET, TS);

        Assert.That(policy.TryAdmit(PEER, PendingPeer(), "0xdef", TS), Is.True);
    }

    [Test]
    public void Disabled_AdmitsEverything()
    {
        HandshakeReplayPolicy policy = Create(enabled: false);

        for (var i = 0; i < 100; i++)
            Assert.That(policy.TryAdmit(PEER, PendingPeer(), WALLET, TS), Is.True);

        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void ZeroPendingAuthTimeout_AlsoDisablesCache()
    {
        // TTL derives from PeerOptions.PendingAuthCleanTimeoutMs; if that's 0 there's no
        // meaningful window to track, so the cache degrades to pass-through.
        HandshakeReplayPolicy policy = Create(pendingAuthTtlMs: 0);

        for (var i = 0; i < 100; i++)
            Assert.That(policy.TryAdmit(PEER, PendingPeer(), WALLET, TS), Is.True);
    }

    [Test]
    public void CacheOverflow_StopsRecording_ButDoesNotRejectFreshHandshakes()
    {
        // With MaxPeers = 2 the cache fills fast. A fresh third pair must still be admitted
        // (no spurious rejection); replay protection for overflowed pairs is lost until entries
        // expire, which is the documented trade-off.
        HandshakeReplayPolicy policy = Create(maxPeers: 2);
        policy.TryAdmit(PEER, PendingPeer(), "0x1", "t1");
        policy.TryAdmit(PEER, PendingPeer(), "0x2", "t2");

        Assert.That(policy.TryAdmit(PEER, PendingPeer(), "0x3", "t3"), Is.True,
            "Overflow must not cause fresh handshakes to be rejected");
    }
}
