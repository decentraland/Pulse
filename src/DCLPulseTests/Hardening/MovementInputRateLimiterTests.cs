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

    private MovementInputRateLimiter Create(int maxHz) =>
        new (Options.Create(new MovementInputRateLimiterOptions { MaxHz = maxHz }), timeProvider, transport);

    private static readonly PeerIndex PEER = new (1);

    [Test]
    public void FirstInput_Accepted()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20);
        timeProvider.MonotonicTime.Returns(1000u);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void InputWithinInterval_Rejected()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20); // 50 ms interval
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);

        timeProvider.MonotonicTime.Returns(1010u); // 10 ms later
        Assert.That(limiter.TryAccept(PEER, state), Is.False);
    }

    [Test]
    public void InputAfterInterval_Accepted()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);

        timeProvider.MonotonicTime.Returns(1050u); // exactly the interval
        Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void LastInputTimestamp_RecordedOnAcceptOnly()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);
        uint firstStamp = state.Throttle.LastInputMs;

        timeProvider.MonotonicTime.Returns(1010u);
        limiter.TryAccept(PEER, state); // rejected
        Assert.That(state.Throttle.LastInputMs, Is.EqualTo(firstStamp),
            "Rejected inputs must not update the last-accepted timestamp");
    }

    [Test]
    public void ZeroHz_Disabled_AlwaysAccepts()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 0);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);

        for (var i = 0; i < 100; i++)
            Assert.That(limiter.TryAccept(PEER, state), Is.True);
    }

    [Test]
    public void Rejection_DisconnectsPeer_WithInputRateExceededReason()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);

        timeProvider.MonotonicTime.Returns(1010u);
        Assert.That(limiter.TryAccept(PEER, state), Is.False);

        transport.Received(1).Disconnect(PEER, DisconnectReason.INPUT_RATE_EXCEEDED);
    }

    [Test]
    public void Rejection_FlipsStateToPendingDisconnect()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20);
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
        MovementInputRateLimiter limiter = Create(maxHz: 20);
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        timeProvider.MonotonicTime.Returns(1000u);
        limiter.TryAccept(PEER, state);

        transport.DidNotReceive().Disconnect(Arg.Any<PeerIndex>(), Arg.Any<DisconnectReason>());
    }

    [Test]
    public void SustainedRate_StaysAtCap()
    {
        MovementInputRateLimiter limiter = Create(maxHz: 20); // 50 ms interval
        var state = new PeerState(PeerConnectionState.AUTHENTICATED);

        var accepted = 0;

        // 1 second of input at 10 ms intervals — expect 20 accepts.
        for (uint t = 0; t < 1000; t += 10)
        {
            timeProvider.MonotonicTime.Returns(t);

            if (limiter.TryAccept(PEER, state))
                accepted++;
        }

        Assert.That(accepted, Is.EqualTo(20));
    }
}
