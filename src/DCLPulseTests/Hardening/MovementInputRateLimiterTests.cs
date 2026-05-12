using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Transport;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class MovementInputRateLimiterTests
{
    private ITimeProvider timeProvider;
    private ITransport transport;

    [SetUp]
    public void SetUp()
    {
        timeProvider = Substitute.For<ITimeProvider>();
        transport = Substitute.For<ITransport>();
    }

    private MovementInputRateLimiter Create(int maxHz, int burstCapacity) =>
        new (Options.Create(new MovementInputRateLimiterOptions
        {
            MaxHz = maxHz,
            BurstCapacity = burstCapacity,
        }), timeProvider, transport);

    private static readonly PeerIndex PEER = new (1);

    [Test]
    public void FirstInputs_FillBurstCapacity_ThenReject()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 3);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.False); // bucket empty
    }

    [Test]
    public void BurstAbsorbsJitter_PacketsArrivingFasterThanInterval_StillAccepted()
    {
        // 20 Hz sustained = 50 ms refill, but 4 packets bunched into a single ms must not
        // trip the limiter — this is the false-positive case the bucket is designed to absorb.
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 4);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void BucketRefills_OneTokenPerInterval()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 1); // 50 ms refill
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state); // consumes the single token

        timeProvider.MonotonicTime.Returns(1025u); // 25 ms later — not yet refilled
        Assert.That(limiter.TryAccept(PEER, state), Is.False);

        timeProvider.MonotonicTime.Returns(1050u); // 50 ms later — one token refilled
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void RefillCapsAtBurstCapacity()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 3); // 50 ms refill
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);
        limiter.TryAccept(PEER, state);
        limiter.TryAccept(PEER, state);
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
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 1); // 50 ms refill
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);

        // 70 ms → one token refilled, 20 ms carried over.
        timeProvider.MonotonicTime.Returns(1070u);
        Assert.That(limiter.TryAccept(PEER, state), Is.True);

        // Another 30 ms (total 100 ms from t=1000) → 50 ms available after remainder.
        timeProvider.MonotonicTime.Returns(1100u);

        Assert.That(limiter.TryAccept(PEER, state), Is.True,
            "Sub-interval elapsed time must carry over to prevent refill drift");
    }

    [Test]
    public void ZeroHz_Disabled_AlwaysAccepts()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 0, burstCapacity: 4);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        for (var i = 0; i < 100; i++)
            Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void ZeroBurstCapacity_Disabled_AlwaysAccepts()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 0);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        for (var i = 0; i < 100; i++)
            Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void Rejection_DisconnectsPeer_WithInputRateExceededReason()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 1);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state); // burns the single token

        timeProvider.MonotonicTime.Returns(1010u);
        Assert.That(limiter.TryAccept(PEER, state), Is.False);

        transport.Received(1).Disconnect(PEER, DisconnectReason.INPUT_RATE_EXCEEDED);
    }

    [Test]
    public void Rejection_FlipsStateToPendingDisconnect()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 1);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);

        timeProvider.MonotonicTime.Returns(1010u);
        limiter.TryAccept(PEER, state);

        Assert.That(state.ConnectionState, Is.EqualTo(PeerConnectionState.PENDING_DISCONNECT));
    }

    [Test]
    public void Acceptance_DoesNotTouchTransport()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 5);
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
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 1000);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        var accepted = 0;

        for (var i = 0; i < 300; i++)
            if (limiter.TryAccept(PEER, state))
                accepted++;

        Assert.That(accepted, Is.EqualTo(255),
            "BurstCapacity > byte.MaxValue must clamp to 255");
    }

    [Test]
    public void SustainedRate_StaysAtCap()
    {
        // Burst of 4 absorbs initial bunching, then long-run rate converges to MaxHz.
        MovementInputRateLimiter limiter = Create(maxHz: 20, burstCapacity: 4); // 50 ms refill
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        var accepted = 0;

        // 10 s of input at 10 ms intervals — bucket starts full, so 4 immediate accepts at
        // t=0..30 burn the burst; subsequent refills land at t=60, 110, … one every 50 ms,
        // giving 199 sustained accepts before t=10000. Total = 4 + 199 = 203.
        for (uint t = 0; t < 10000; t += 10)
        {
            timeProvider.MonotonicTime.Returns(t);
            if (limiter.TryAccept(PEER, state)) accepted++;
        }

        Assert.That(accepted, Is.EqualTo(203));
    }
}
