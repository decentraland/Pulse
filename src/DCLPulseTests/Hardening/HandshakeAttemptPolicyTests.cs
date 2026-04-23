using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class HandshakeAttemptPolicyTests
{
    private static readonly PeerIndex PEER = new (1);

    private ITransport transport;

    [SetUp]
    public void SetUp()
    {
        transport = Substitute.For<ITransport>();
    }

    private HandshakeAttemptPolicy Create(byte maxAttempts) =>
        new (Options.Create(new HandshakeAttemptPolicyOptions { MaxAttempts = maxAttempts }), transport);

    private static PeerState NewPendingPeer() =>
        new (PeerConnectionState.PENDING_AUTH);

    [Test]
    public void FirstAttempts_UnderLimit_Succeed()
    {
        HandshakeAttemptPolicy policy = Create(maxAttempts: 2);
        PeerState state = NewPendingPeer();

        Assert.That(policy.TryRecordAttempt(PEER, state), Is.True);
        Assert.That(policy.TryRecordAttempt(PEER, state), Is.True);
    }

    [Test]
    public void AttemptBeyondLimit_RejectedAndDisconnectsPeer()
    {
        HandshakeAttemptPolicy policy = Create(maxAttempts: 2);
        PeerState state = NewPendingPeer();

        policy.TryRecordAttempt(PEER, state);
        policy.TryRecordAttempt(PEER, state);

        Assert.That(policy.TryRecordAttempt(PEER, state), Is.False);
        transport.Received(1).Disconnect(PEER, DisconnectReason.AUTH_FAILED);
        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
    }

    [Test]
    public void CounterPersistsOnPeerState()
    {
        HandshakeAttemptPolicy policy = Create(maxAttempts: 3);
        PeerState state = NewPendingPeer();

        policy.TryRecordAttempt(PEER, state);
        policy.TryRecordAttempt(PEER, state);

        Assert.That(state.TransportState.HandshakeAttempts, Is.EqualTo(2));
    }

    [Test]
    public void ZeroLimit_DisablesPolicy_NoCounterMutation_NoDisconnect()
    {
        HandshakeAttemptPolicy policy = Create(maxAttempts: 0);
        PeerState state = NewPendingPeer();

        Assert.That(policy.IsEnabled, Is.False);

        for (var i = 0; i < 100; i++)
            Assert.That(policy.TryRecordAttempt(PEER, state), Is.True);

        Assert.That(state.TransportState.HandshakeAttempts, Is.EqualTo(0),
            "Disabled policy must not mutate the per-peer counter");
        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }
}
