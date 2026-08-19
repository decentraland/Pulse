using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.Peers;
using Pulse.Transport.Hardening;

namespace DCLPulseTests.Hardening;

/// <summary>
///     Behaviour of the hard per-source-IP concurrent-connection cap. The defining property is
///     "always count, gate only enforcement": disabling the limiter, zeroing the cap or
///     whitelisting an IP must never stop the bookkeeping, otherwise re-enabling would resume
///     from a zero baseline and over-admit.
/// </summary>
[TestFixture]
public class IpLimiterTests
{
    private const string IP = "203.0.113.1";
    private const string OTHER_IP = "203.0.113.2";

    // ── Cap enforcement ──────────────────────────────────────────────

    [Test]
    public void AdmitsUpToCap_ThenRefuses()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 3);

        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False);
    }

    [Test]
    public void DifferentIps_CountedIndependently()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP), Is.False);
    }

    [Test]
    public void ZeroMaxConcurrency_DisablesEnforcement()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 0);

        for (var i = 0; i < 100; i++)
            Assert.That(harness.Limiter.TryAcquire(IP), Is.True);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "A disabled cap must still track the IP so re-enabling sees the real population");
    }

    [Test]
    public void Disabled_StillCounts_SoReEnableIsAccurate()
    {
        using Harness harness = Create(enabled: false, maxConcurrency: 2);

        // Enforcement off: five connections from one IP sail past a cap of two.
        for (var i = 0; i < 5; i++)
            Assert.That(harness.Limiter.TryAcquire(IP), Is.True);

        harness.Reconfigure(o => o.Enabled = true);

        Assert.That(harness.Limiter.TryAcquire(IP), Is.False,
            "Counting must continue while disabled — re-enabling has to refuse against the live count, not a zero baseline");
    }

    [Test]
    public void ZeroMaxConcurrency_RaisedAtRuntime_EnforcesAgainstLiveCount()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 0);

        for (var i = 0; i < 5; i++)
            Assert.That(harness.Limiter.TryAcquire(IP), Is.True);

        harness.Reconfigure(o => o.MaxConcurrency = 2);

        Assert.That(harness.Limiter.TryAcquire(IP), Is.False,
            "A zero cap disables refusal only — the five existing connections must already be on the books");
    }

    [Test]
    public void MaxConcurrencyLowered_DoesNotEvictExisting()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 5);

        for (var i = 0; i < 4; i++)
            Assert.That(harness.Limiter.TryAcquire(IP), Is.True);

        harness.Reconfigure(o => o.MaxConcurrency = 2);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "Lowering the cap must not drop the IP's bookkeeping");
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False,
            "New connections are refused while the IP sits above the lowered cap");

        // The four live reservations survived: draining to exactly the new cap still refuses…
        harness.Limiter.Abandon(IP);
        harness.Limiter.Abandon(IP);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False);

        // …and only dropping below it admits again.
        harness.Limiter.Abandon(IP);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
    }

    // ── Whitelist ────────────────────────────────────────────────────

    [Test]
    public void WhitelistedIp_ExceedsCap_IsAdmitted()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2, whitelist: IP);

        for (var i = 0; i < 10; i++)
            Assert.That(harness.Limiter.TryAcquire(IP), Is.True);

        Assert.That(harness.Limiter.TryAcquire(OTHER_IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP), Is.False,
            "The whitelist exempts only the listed IP");
    }

    [Test]
    public void WhitelistedIp_IsStillCounted()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2, whitelist: IP);

        for (var i = 0; i < 5; i++)
            Assert.That(harness.Limiter.TryAcquire(IP), Is.True);

        harness.Reconfigure(o => o.Whitelist = "");

        Assert.That(harness.Limiter.TryAcquire(IP), Is.False,
            "Whitelisted connections are counted, so de-whitelisting applies the cap immediately");
    }

    [Test]
    public void WhitelistChangedAtRuntime_TakesEffect()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False);

        harness.Reconfigure(o => o.Whitelist = $"{OTHER_IP}, {IP}");

        Assert.That(harness.Limiter.TryAcquire(IP), Is.True,
            "The OnChange callback must rebuild the whitelist snapshot");
    }

    [Test]
    public void WhitelistChanged_IsLogged()
    {
        // The exemption list is the one knob that silently widens the cap, so every transition
        // has to be on the record.
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        harness.Reconfigure(o => o.Whitelist = "10.0.0.1, 10.0.0.2");

        harness.Logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Test]
    public void UnrelatedOptionChanged_DoesNotLogWhitelist()
    {
        // OnChange fires for every knob in the section and once per configuration provider. Logging
        // per callback would put a whitelist line in the log every time MaxConcurrency moved.
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        harness.Reconfigure(o => o.MaxConcurrency = 5);

        harness.Logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Test]
    public void WhitelistMatchIsCaseInsensitive()
    {
        // IPv6 hex literals are the reason the whitelist comparer is ordinal-ignore-case.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, whitelist: "FE80::AB");

        Assert.That(harness.Limiter.TryAcquire("fe80::ab"), Is.True);
        Assert.That(harness.Limiter.TryAcquire("fe80::ab"), Is.True,
            "A whitelist entry must match regardless of hex casing");
    }

    [Test]
    public void SameIpDifferentCasing_SharesOneCounter()
    {
        // One IPv6 address arrives with different hex casing from each transport. Both must debit
        // the same budget, or one address holds twice the cap by alternating transports.
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire("2001:DB8::1"), Is.True);
        Assert.That(harness.Limiter.TryAcquire("2001:db8::1"), Is.False,
            "Differently-cased spellings of one address must share a counter");
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1));
    }

    // ── Release, Abandon and entry lifetime ──────────────────────────

    [Test]
    public void Release_FreesSlot()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 1);
        var peer = new PeerIndex(7);

        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        harness.Limiter.Bind(peer, IP);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False);

        harness.Limiter.Release(peer);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
    }

    [Test]
    public void ReleaseUnboundPeer_IsNoOp()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2);

        harness.Limiter.TryAcquire(IP);

        Assert.DoesNotThrow(() => harness.Limiter.Release(new PeerIndex(999)));

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "Release is keyed by PeerIndex — an unknown peer holds no binding and decrements nothing");
    }

    [Test]
    public void DoubleRelease_IsNoOp()
    {
        // Release is idempotent by construction: the PeerIndex→IP binding is looked up and
        // cleared in one step, so the second call finds nothing to decrement.
        using Harness harness = Create(enabled: true, maxConcurrency: 2);
        var first = new PeerIndex(1);
        var second = new PeerIndex(2);

        harness.Limiter.TryAcquire(IP);
        harness.Limiter.Bind(first, IP);
        harness.Limiter.TryAcquire(IP);
        harness.Limiter.Bind(second, IP);

        harness.Limiter.Release(first);
        harness.Limiter.Release(first);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "The second peer's reservation must survive a duplicate release of the first");
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False,
            "Exactly one slot was freed, so the cap is reached again after a single acquire");
    }

    [Test]
    public void Abandon_FreesUnboundReservation()
    {
        // The transport rollback path: allocator exhaustion or a PreAuthAdmission refusal happens
        // before Bind, so there is no PeerIndex to release against.
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False);

        harness.Limiter.Abandon(IP);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
    }

    [Test]
    public void Abandon_IpHoldingNoReservations_IsNoOp()
    {
        // Keyed by IP alone, so unlike Release it cannot be idempotent by construction — the
        // contract is at most one Abandon per successful TryAcquire. Out of contract it does
        // nothing: no throw, and no underflow that would widen the cap.
        using Harness harness = Create(enabled: true, maxConcurrency: 2);

        Assert.DoesNotThrow(() => harness.Limiter.Abandon("198.51.100.7"));
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));

        harness.Limiter.TryAcquire(IP);
        harness.Limiter.Abandon(IP);
        harness.Limiter.Abandon(IP);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP), Is.False,
            "The surplus Abandon must not have pushed the count negative — the full cap is still enforced");
    }

    [Test]
    public void PerIpEntry_RemovedAtZero()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2);

        for (var i = 0; i < 500; i++)
        {
            string ip = "198.51.100." + i;

            harness.Limiter.TryAcquire(ip);
            harness.Limiter.TryAcquire(ip);
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1));

            harness.Limiter.Abandon(ip);
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
                "The entry survives while the IP still holds a reservation");

            harness.Limiter.Abandon(ip);
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0),
                "The entry is removed at zero, so the map is bounded by concurrent connections, not by distinct IPs seen");
        }
    }

    // ── Concurrency ──────────────────────────────────────────────────

    [Test]
    public void Concurrent_RespectsCap()
    {
        // ENet and WebTransport call TryAcquire from separate threads, so the lock is
        // load-bearing: under contention the gate must admit exactly CAP, never more.
        const int CAP = 100;
        const int THREADS = 16;
        const int PER_THREAD = 50;

        using Harness harness = Create(enabled: true, maxConcurrency: CAP);

        var admits = 0;
        var barrier = new Barrier(THREADS);

        Parallel.For(0, THREADS, _ =>
        {
            barrier.SignalAndWait();

            for (var i = 0; i < PER_THREAD; i++)
                if (harness.Limiter.TryAcquire(IP))
                    Interlocked.Increment(ref admits);
        });

        Assert.That(admits, Is.EqualTo(CAP),
            "Under contention the cap must admit exactly CAP connections — no oversubscription");
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1));
    }

    [Test]
    public void Concurrent_AcquireAndRelease_LeavesNoResidue()
    {
        const int THREADS = 16;
        const int PER_THREAD = 100;

        using Harness harness = Create(enabled: true, maxConcurrency: 0);

        var barrier = new Barrier(THREADS);

        Parallel.For(0, THREADS, tid =>
        {
            barrier.SignalAndWait();

            for (var i = 0; i < PER_THREAD; i++)
            {
                var peer = new PeerIndex((uint)((tid * PER_THREAD) + i));

                harness.Limiter.TryAcquire(IP);
                harness.Limiter.Bind(peer, IP);
                harness.Limiter.Release(peer);
            }
        });

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0),
            "Every reservation was released, so the per-IP entry must have been removed at zero");
    }

    // ── Lifetime ─────────────────────────────────────────────────────

    [Test]
    public void Dispose_UnsubscribesFromOptionsMonitor()
    {
        Harness harness = Create(enabled: true, maxConcurrency: 1);

        harness.Limiter.Dispose();

        harness.Subscription.Received(1).Dispose();
    }

    private static Harness Create(bool enabled, int maxConcurrency, string whitelist = "") =>
        new (enabled, maxConcurrency, whitelist);

    /// <summary>
    ///     Wires an <see cref="IpLimiter" /> to a substituted <see cref="IOptionsMonitor{T}" />,
    ///     capturing the change callback the constructor registers so tests can reconfigure the
    ///     limiter the way the runtime feature-flag document does.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<Action<IpLimiterOptions, string?>> listeners = new ();
        private readonly IpLimiterOptions options;

        public IDisposable Subscription { get; }

        public IpLimiter Limiter { get; }

        public Harness(bool enabled, int maxConcurrency, string whitelist)
        {
            options = new IpLimiterOptions
            {
                Enabled = enabled,
                MaxConcurrency = maxConcurrency,
                Whitelist = whitelist,
            };

            Subscription = Substitute.For<IDisposable>();

            IOptionsMonitor<IpLimiterOptions> monitor = Substitute.For<IOptionsMonitor<IpLimiterOptions>>();
            monitor.CurrentValue.Returns(_ => options);

            monitor.OnChange(Arg.Any<Action<IpLimiterOptions, string?>>())
                   .Returns(call =>
                    {
                        listeners.Add(call.Arg<Action<IpLimiterOptions, string?>>());
                        return Subscription;
                    });

            Logger = Substitute.For<ILogger<IpLimiter>>();
            Limiter = new IpLimiter(monitor, Logger);
        }

        public ILogger<IpLimiter> Logger { get; }

        public void Dispose() =>
            Limiter.Dispose();

        /// <summary>
        ///     Mutates the options instance the monitor hands out and fires every registered
        ///     change callback, mirroring an <c>IOptionsMonitor</c> reload.
        /// </summary>
        public void Reconfigure(Action<IpLimiterOptions> mutate)
        {
            mutate(options);

            foreach (Action<IpLimiterOptions, string?> listener in listeners)
                listener(options, null);
        }
    }
}
