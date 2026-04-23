using Microsoft.Extensions.Options;
using Pulse.Peers;
using Pulse.Transport.Hardening;

namespace DCLPulseTests.Hardening;

[TestFixture]
public class PreAuthAdmissionTests
{
    private static PreAuthAdmission Create(int budget, int perIpCap) =>
        new (Options.Create(new PreAuthAdmissionOptions
        {
            PreAuthBudget = budget,
            MaxConcurrentPreAuthPerIP = perIpCap,
        }));

    // ── Global budget ────────────────────────────────────────────────

    [Test]
    public void AdmitsUpToBudget_ThenRefusesWithBudgetExhausted()
    {
        PreAuthAdmission admission = Create(budget: 3, perIpCap: 0);

        Assert.That(admission.TryAdmit(new PeerIndex(0), "a"), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(1), "b"), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(2), "c"), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(3), "d"), Is.EqualTo(PreAuthAdmission.AdmitResult.BUDGET_EXHAUSTED));
        Assert.That(admission.InFlight, Is.EqualTo(3));
    }

    [Test]
    public void ZeroBudget_DisablesGlobalCap()
    {
        PreAuthAdmission admission = Create(budget: 0, perIpCap: 0);

        for (uint i = 0; i < 1000; i++)
            Assert.That(admission.TryAdmit(new PeerIndex(i), "ip-" + i), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
    }

    // ── Per-IP cap ───────────────────────────────────────────────────

    [Test]
    public void PerIp_AdmitsUpToCap_ThenRefusesSameIp()
    {
        PreAuthAdmission admission = Create(budget: 0, perIpCap: 3);
        const string IP = "203.0.113.1";

        Assert.That(admission.TryAdmit(new PeerIndex(0), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(1), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(2), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(3), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.IP_LIMIT_EXHAUSTED));
    }

    [Test]
    public void PerIp_DifferentIps_CountedIndependently()
    {
        PreAuthAdmission admission = Create(budget: 0, perIpCap: 1);

        Assert.That(admission.TryAdmit(new PeerIndex(0), "203.0.113.1"), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(1), "203.0.113.2"), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(2), "203.0.113.3"), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
    }

    [Test]
    public void PerIpZero_DisablesPerIpCapOnly()
    {
        PreAuthAdmission admission = Create(budget: 3, perIpCap: 0);
        const string IP = "203.0.113.1";

        // Global budget should still refuse once 3 peers are in — per-IP is disabled.
        Assert.That(admission.TryAdmit(new PeerIndex(0), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(1), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(2), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(3), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.BUDGET_EXHAUSTED));
    }

    [Test]
    public void PerIpCheckedBeforeBudget()
    {
        // Single IP with cap 1; global budget of 100 — per-IP refusal must fire first.
        PreAuthAdmission admission = Create(budget: 100, perIpCap: 1);
        const string IP = "203.0.113.1";

        admission.TryAdmit(new PeerIndex(0), IP);
        Assert.That(admission.TryAdmit(new PeerIndex(1), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.IP_LIMIT_EXHAUSTED));
    }

    // ── Release semantics ────────────────────────────────────────────

    [Test]
    public void ReleaseOnDisconnect_FreesBothCounters()
    {
        PreAuthAdmission admission = Create(budget: 1, perIpCap: 1);
        const string IP = "203.0.113.1";

        admission.TryAdmit(new PeerIndex(0), IP);
        Assert.That(admission.InFlight, Is.EqualTo(1));

        admission.ReleaseOnDisconnect(new PeerIndex(0));

        Assert.That(admission.InFlight, Is.EqualTo(0));
        Assert.That(admission.TryAdmit(new PeerIndex(1), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
    }

    [Test]
    public void ReleaseOnPromotion_FreesBothCounters()
    {
        PreAuthAdmission admission = Create(budget: 1, perIpCap: 1);
        const string IP = "203.0.113.1";

        admission.TryAdmit(new PeerIndex(0), IP);
        admission.ReleaseOnPromotion(new PeerIndex(0));

        Assert.That(admission.InFlight, Is.EqualTo(0));
        Assert.That(admission.TryAdmit(new PeerIndex(1), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
    }

    [Test]
    public void Promotion_ThenDisconnect_DoesNotDoubleDecrement()
    {
        // A peer that promoted out of PENDING_AUTH and later disconnects — Disconnect must be a no-op.
        PreAuthAdmission admission = Create(budget: 0, perIpCap: 2);
        const string IP = "203.0.113.1";

        admission.TryAdmit(new PeerIndex(0), IP);
        admission.TryAdmit(new PeerIndex(1), IP);
        admission.ReleaseOnPromotion(new PeerIndex(0));
        admission.ReleaseOnDisconnect(new PeerIndex(0));

        // Peer 1 still occupies its per-IP slot → one more peer can admit before cap.
        Assert.That(admission.TryAdmit(new PeerIndex(2), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.OK));
        Assert.That(admission.TryAdmit(new PeerIndex(3), IP), Is.EqualTo(PreAuthAdmission.AdmitResult.IP_LIMIT_EXHAUSTED));
    }

    [Test]
    public void ReleaseUnknownPeer_IsNoOp()
    {
        PreAuthAdmission admission = Create(budget: 1, perIpCap: 1);

        Assert.DoesNotThrow(() => admission.ReleaseOnDisconnect(new PeerIndex(999)));
        Assert.DoesNotThrow(() => admission.ReleaseOnPromotion(new PeerIndex(999)));
        Assert.That(admission.InFlight, Is.EqualTo(0));
    }

    // ── Concurrency ──────────────────────────────────────────────────

    [Test]
    public void Concurrent_TryAdmit_RespectsBudget()
    {
        const int BUDGET = 100;
        const int THREADS = 16;
        const int PER_THREAD = 50;

        PreAuthAdmission admission = Create(budget: BUDGET, perIpCap: 0);

        var admits = 0;
        var barrier = new Barrier(THREADS);

        Parallel.For(0, THREADS, tid =>
        {
            barrier.SignalAndWait();

            for (var i = 0; i < PER_THREAD; i++)
            {
                var peerId = (uint)((tid * PER_THREAD) + i);

                if (admission.TryAdmit(new PeerIndex(peerId), "ip-" + tid) == PreAuthAdmission.AdmitResult.OK)
                    Interlocked.Increment(ref admits);
            }
        });

        Assert.That(admits, Is.EqualTo(BUDGET),
            "Under contention the gate must admit exactly BUDGET peers — no oversubscription.");
        Assert.That(admission.InFlight, Is.EqualTo(BUDGET));
    }
}
