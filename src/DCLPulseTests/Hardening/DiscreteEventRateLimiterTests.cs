using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class DiscreteEventRateLimiterTests
{
    private ITimeProvider timeProvider;
    private ITransport transport;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        transport = Substitute.For<ITransport>();
    }

    private DiscreteEventRateLimiter Create(double ratePerSecond, int burstCapacity) =>
        new (Options.Create(new DiscreteEventRateLimiterOptions
        {
            RatePerSecond = ratePerSecond,
            BurstCapacity = burstCapacity,
        }), timeProvider, transport);

    private static readonly PeerIndex PEER = new (1);

    [Test]
    public void FirstEvents_FillBurstCapacity_ThenReject()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 3);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.False); // bucket empty
    }

    [Test]
    public void BucketRefills_OneTokenPerInterval()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 1); // 200 ms refill
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state); // consumes the single token

        timeProvider.MonotonicTime.Returns(1100u); // 100 ms later — not yet refilled
        Assert.That(limiter.TryAccept(PEER, state), Is.False);

        timeProvider.MonotonicTime.Returns(1200u); // 200 ms later — one token refilled
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void RefillCapsAtBurstCapacity()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 3); // 200 ms refill
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state); limiter.TryAccept(PEER, state); limiter.TryAccept(PEER, state);
        Assert.That(limiter.TryAccept(PEER, state), Is.False);

        // Wait 10 seconds — should not overflow burst capacity.
        timeProvider.MonotonicTime.Returns(11000u);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.False,
            "Refill must never exceed burst capacity");
    }

    [Test]
    public void RefillPreservesRemainder_NoDrift()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 1); // 200 ms refill
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);

        // 250 ms → one token refilled, 50 ms carried over.
        timeProvider.MonotonicTime.Returns(1250u);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);

        // Another 150 ms (total 400 ms) → 200 ms available after remainder, second refill.
        timeProvider.MonotonicTime.Returns(1400u);
        Assert.That(limiter.TryAccept(PEER, state), Is.True,
            "Sub-interval elapsed time must carry over to prevent refill drift");
    }

    [Test]
    public void ZeroRate_Disabled_AlwaysAccepts()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 0, burstCapacity: 10);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        for (var i = 0; i < 100; i++)
            Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void ZeroBurstCapacity_Disabled_AlwaysAccepts()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 0);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        for (var i = 0; i < 100; i++)
            Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void Rejection_DisconnectsPeer_WithDiscreteEventRateExceededReason()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 1);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state); // burns the single token

        timeProvider.MonotonicTime.Returns(1010u);
        Assert.That(limiter.TryAccept(PEER, state), Is.False);

        transport.Received(1).Disconnect(PEER, DisconnectReason.DISCRETE_EVENT_RATE_EXCEEDED);
    }

    [Test]
    public void Acceptance_DoesNotTouchTransport()
    {
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 5);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);
        limiter.TryAccept(PEER, state);

        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void BurstCapacityAbove255_ClampsToByteMax()
    {
        // Sanity: the byte-backed token field must not wrap silently on overflow.
        DiscreteEventRateLimiter limiter = Create(ratePerSecond: 5, burstCapacity: 1000);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        var accepted = 0;
        for (var i = 0; i < 300; i++)
            if (limiter.TryAccept(PEER, state)) accepted++;

        Assert.That(accepted, Is.EqualTo(255),
            "BurstCapacity > byte.MaxValue must clamp to 255");
    }
}
