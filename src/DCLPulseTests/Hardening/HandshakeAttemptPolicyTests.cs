using Microsoft.Extensions.Options;
using Pulse.Messaging.Hardening;
using Pulse.Peers;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class HandshakeAttemptPolicyTests
{
    private static HandshakeAttemptPolicy CreatePolicy(byte maxAttempts) =>
        new (Options.Create(new HandshakeAttemptPolicyOptions { MaxAttempts = maxAttempts }));

    private static PeerState NewPendingPeer() =>
        new (PeerConnectionState.PENDING_AUTH);

    [Test]
    public void FirstAttempts_UnderLimit_Succeed()
    {
        HandshakeAttemptPolicy policy = CreatePolicy(maxAttempts: 2);
        PeerState state = NewPendingPeer();

        Assert.That(policy.TryRecordAttempt(state), Is.True);
        Assert.That(policy.TryRecordAttempt(state), Is.True);
    }

    [Test]
    public void AttemptBeyondLimit_Rejected()
    {
        HandshakeAttemptPolicy policy = CreatePolicy(maxAttempts: 2);
        PeerState state = NewPendingPeer();

        policy.TryRecordAttempt(state);
        policy.TryRecordAttempt(state);

        Assert.That(policy.TryRecordAttempt(state), Is.False);
    }

    [Test]
    public void CounterPersistsOnPeerState()
    {
        HandshakeAttemptPolicy policy = CreatePolicy(maxAttempts: 3);
        PeerState state = NewPendingPeer();

        policy.TryRecordAttempt(state);
        policy.TryRecordAttempt(state);

        Assert.That(state.TransportState.HandshakeAttempts, Is.EqualTo(2));
    }

    [Test]
    public void ZeroLimit_DisablesPolicy_NoCounterMutation()
    {
        HandshakeAttemptPolicy policy = CreatePolicy(maxAttempts: 0);
        PeerState state = NewPendingPeer();

        Assert.That(policy.IsEnabled, Is.False);

        for (var i = 0; i < 100; i++)
            Assert.That(policy.TryRecordAttempt(state), Is.True);

        Assert.That(state.TransportState.HandshakeAttempts, Is.EqualTo(0),
            "Disabled policy must not mutate the per-peer counter");
    }
}
