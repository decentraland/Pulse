using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse;
using Pulse.Peers;
using Pulse.Transport.Hardening;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class CorruptedPacketLimiterTests
{
    private ITimeProvider timeProvider;

    [SetUp]
    public void SetUp() => timeProvider = Substitute.For<ITimeProvider>();

    private CorruptedPacketLimiter Create(int maxPerMinute, int burstCapacity) =>
        new (Options.Create(new CorruptedPacketLimiterOptions
        {
            MaxPerMinute = maxPerMinute,
            BurstCapacity = burstCapacity,
        }), timeProvider);

    private static readonly PeerIndex PEER = new (1);

    [Test]
    public void BurstAbsorbed_ThenExhausts()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 3);
        timeProvider.MonotonicTime.Returns(1000u);

        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.True, "bucket should exhaust on the fourth corrupt packet");
    }

    [Test]
    public void RefillsOneTokenPerInterval()
    {
        // 5 per minute → 12000 ms refill interval.
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 1);
        timeProvider.MonotonicTime.Returns(1000u);

        limiter.RegisterAndCheckExhausted(PEER); // consumes the single token

        timeProvider.MonotonicTime.Returns(1000u + 6000u); // half an interval — no refill
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.True);

        timeProvider.MonotonicTime.Returns(1000u + 12000u); // one full interval — refilled
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
    }

    [Test]
    public void RefillCapsAtBurstCapacity()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 3); // 12000 ms refill
        timeProvider.MonotonicTime.Returns(1000u);

        limiter.RegisterAndCheckExhausted(PEER);
        limiter.RegisterAndCheckExhausted(PEER);
        limiter.RegisterAndCheckExhausted(PEER);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.True);

        // Idle for an hour — should not exceed burst capacity.
        timeProvider.MonotonicTime.Returns(1000u + 3_600_000u);

        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.True,
            "refill must clamp to burst capacity, even after a long idle window");
    }

    [Test]
    public void RefillPreservesRemainder_NoDrift()
    {
        // 12000 ms refill, burst 1.
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 1);
        timeProvider.MonotonicTime.Returns(1000u);

        limiter.RegisterAndCheckExhausted(PEER);

        // 15000 ms later — one token refilled, 3000 ms carry.
        timeProvider.MonotonicTime.Returns(1000u + 15_000u);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);

        // Another 9000 ms (total 24_000) — combined with the 3000 ms remainder this is exactly
        // one more refill interval, so we must accept here, not on the next call.
        timeProvider.MonotonicTime.Returns(1000u + 24_000u);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
    }

    [Test]
    public void ZeroMaxPerMinute_AlwaysAllows()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 0, burstCapacity: 5);
        timeProvider.MonotonicTime.Returns(1000u);

        for (var i = 0; i < 100; i++)
            Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
    }

    [Test]
    public void ZeroBurstCapacity_AlwaysAllows()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 0);
        timeProvider.MonotonicTime.Returns(1000u);

        for (var i = 0; i < 100; i++)
            Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
    }

    [Test]
    public void Release_ResetsPerPeerBudget()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 2);
        timeProvider.MonotonicTime.Returns(1000u);

        limiter.RegisterAndCheckExhausted(PEER);
        limiter.RegisterAndCheckExhausted(PEER);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.True);

        // After release, the peer slot is gone — a reconnect on the same PeerIndex starts fresh.
        limiter.Release(PEER);

        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(PEER), Is.False);
    }

    [Test]
    public void Release_UnknownPeer_IsNoOp()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 2);
        Assert.DoesNotThrow(() => limiter.Release(new PeerIndex(999)));
    }

    [Test]
    public void DifferentPeers_TrackedIndependently()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 1);
        var a = new PeerIndex(1);
        var b = new PeerIndex(2);
        timeProvider.MonotonicTime.Returns(1000u);

        Assert.That(limiter.RegisterAndCheckExhausted(a), Is.False);
        Assert.That(limiter.RegisterAndCheckExhausted(a), Is.True);

        // b's bucket is independent — first corrupt packet must not exhaust.
        Assert.That(limiter.RegisterAndCheckExhausted(b), Is.False);
    }

    [Test]
    public void BurstCapacityAbove255_ClampsToByteMax()
    {
        CorruptedPacketLimiter limiter = Create(maxPerMinute: 5, burstCapacity: 1000);
        timeProvider.MonotonicTime.Returns(1000u);

        var nonExhausting = 0;

        for (var i = 0; i < 300; i++)
            if (!limiter.RegisterAndCheckExhausted(PEER))
                nonExhausting++;

        Assert.That(nonExhausting, Is.EqualTo(255),
            "BurstCapacity > byte.MaxValue must clamp to 255");
    }
}
