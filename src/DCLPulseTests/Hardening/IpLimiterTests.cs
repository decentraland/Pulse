using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pulse.Peers;
using Pulse.Transport.Hardening;
using System.Diagnostics.Metrics;

namespace DCLPulseTests.Hardening;

/// <summary>
///     Behaviour of the hard per-source-IP concurrent-connection cap. The defining property is
///     "always count, gate only enforcement": disabling the limiter, zeroing the cap or
///     whitelisting an IP must never stop the bookkeeping, otherwise re-enabling would resume
///     from a zero baseline and over-admit.
///     <para />
///     The cap is per <see cref="ConnectionClass" />, and a connection's class is only known once
///     it announces itself — so the second property under test is that a reservation moves between
///     budgets whole or not at all.
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

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False);
    }

    [Test]
    public void DifferentIps_CountedIndependently()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP, ConnectionClass.PLAYER), Is.False);
    }

    [Test]
    public void ZeroMaxConcurrency_DisablesEnforcement()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 0);

        for (var i = 0; i < 100; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "A disabled cap must still track the IP so re-enabling sees the real population");
    }

    [Test]
    public void Disabled_StillCounts_SoReEnableIsAccurate()
    {
        using Harness harness = Create(enabled: false, maxConcurrency: 2);

        // Enforcement off: five connections from one IP sail past a cap of two.
        for (var i = 0; i < 5; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);

        harness.Reconfigure(o => o.Enabled = true);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "Counting must continue while disabled — re-enabling has to refuse against the live count, not a zero baseline");
    }

    [Test]
    public void ZeroMaxConcurrency_RaisedAtRuntime_EnforcesAgainstLiveCount()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 0);

        for (var i = 0; i < 5; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);

        harness.Reconfigure(o => o.MaxConcurrency = 2);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "A zero cap disables refusal only — the five existing connections must already be on the books");
    }

    [Test]
    public void MaxConcurrencyLowered_DoesNotEvictExisting()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 5);

        for (var i = 0; i < 4; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);

        harness.Reconfigure(o => o.MaxConcurrency = 2);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "Lowering the cap must not drop the IP's bookkeeping");
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "New connections are refused while the IP sits above the lowered cap");

        // The four live reservations survived: draining to exactly the new cap still refuses…
        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);
        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False);

        // …and only dropping below it admits again.
        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
    }

    // ── Whitelist ────────────────────────────────────────────────────

    [Test]
    public void WhitelistedIp_ExceedsCap_IsAdmitted()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2, whitelist: IP);

        for (var i = 0; i < 10; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);

        Assert.That(harness.Limiter.TryAcquire(OTHER_IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP, ConnectionClass.PLAYER), Is.False,
            "The whitelist exempts only the listed IP");
    }

    [Test]
    public void WhitelistedIp_IsStillCounted()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2, whitelist: IP);

        for (var i = 0; i < 5; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);

        harness.Reconfigure(o => o.Whitelist = "");

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "Whitelisted connections are counted, so de-whitelisting applies the cap immediately");
    }

    [Test]
    public void WhitelistChangedAtRuntime_TakesEffect()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False);

        harness.Reconfigure(o => o.Whitelist = $"{OTHER_IP}, {IP}");

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
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

        Assert.That(harness.Limiter.TryAcquire("fe80::ab", ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire("fe80::ab", ConnectionClass.PLAYER), Is.True,
            "A whitelist entry must match regardless of hex casing");
    }

    [Test]
    public void SameIpDifferentCasing_SharesOneCounter()
    {
        // One IPv6 address arrives with different hex casing from each transport. Both must debit
        // the same budget, or one address holds twice the cap by alternating transports.
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire("2001:DB8::1", ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire("2001:db8::1", ConnectionClass.PLAYER), Is.False,
            "Differently-cased spellings of one address must share a counter");
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1));
    }

    // ── Release, Abandon and entry lifetime ──────────────────────────

    [Test]
    public void Release_FreesSlot()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 1);
        var peer = new PeerIndex(7);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False);

        harness.Limiter.Release(peer);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
    }

    [Test]
    public void ReleaseUnboundPeer_IsNoOp()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2);

        harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER);

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

        harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER);
        harness.Limiter.Bind(first, IP, ConnectionClass.PLAYER);
        harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER);
        harness.Limiter.Bind(second, IP, ConnectionClass.PLAYER);

        harness.Limiter.Release(first);
        harness.Limiter.Release(first);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "The second peer's reservation must survive a duplicate release of the first");
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "Exactly one slot was freed, so the cap is reached again after a single acquire");
    }

    [Test]
    public void Abandon_FreesUnboundReservation()
    {
        // The transport rollback path: allocator exhaustion or a PreAuthAdmission refusal happens
        // before Bind, so there is no PeerIndex to release against.
        using Harness harness = Create(enabled: true, maxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False);

        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
    }

    [Test]
    public void Abandon_IpHoldingNoReservations_IsNoOp()
    {
        // Keyed by IP alone, so unlike Release it cannot be idempotent by construction — the
        // contract is at most one Abandon per successful TryAcquire. Out of contract it does
        // nothing: no throw, and no underflow that would widen the cap.
        using Harness harness = Create(enabled: true, maxConcurrency: 2);

        Assert.DoesNotThrow(() => harness.Limiter.Abandon("198.51.100.7", ConnectionClass.PLAYER));
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));

        harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER);
        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);
        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "The surplus Abandon must not have pushed the count negative — the full cap is still enforced");
    }

    [Test]
    public void PerIpEntry_RemovedAtZero()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 2);

        for (var i = 0; i < 500; i++)
        {
            string ip = "198.51.100." + i;

            harness.Limiter.TryAcquire(ip, ConnectionClass.PLAYER);
            harness.Limiter.TryAcquire(ip, ConnectionClass.PLAYER);
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1));

            harness.Limiter.Abandon(ip, ConnectionClass.PLAYER);
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
                "The entry survives while the IP still holds a reservation");

            harness.Limiter.Abandon(ip, ConnectionClass.PLAYER);
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0),
                "The entry is removed at zero, so the map is bounded by concurrent connections, not by distinct IPs seen");
        }
    }

    // ── Per-class budgets ────────────────────────────────────────────

    [Test]
    public void ListenerBudget_IsIndependentOfPlayerBudget()
    {
        // A listener fleet runs from few egress IPs, so its connections must not be paid for out of
        // the player budget — and must not be able to spend it either.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "The player budget is full");
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True,
            "A full player budget must not close the listener budget");
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False,
            "The listener budget has its own cap");
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "Both classes of one IP share a single tracked-IP entry");
    }

    [Test]
    public void PlayerBudget_IsIndependentOfListenerBudget()
    {
        // The same property in the other direction: exhausting the listener cap must not refuse the
        // next real player from that IP.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
            "A full listener budget must not close the player budget");
    }

    [Test]
    public void Reclassify_MovesTheReservationBetweenBudgets()
    {
        // The two-phase reservation: the connect gate charged PLAYER because the class is unknowable
        // that early, and the listener handshake moves the same slot across. A move that only
        // charged the target, or only credited the source, would silently widen one of the caps.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 1);
        var peer = new PeerIndex(11);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);
        Assume.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "Precondition: the single player slot is taken");

        Assert.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False,
                "The moved reservation now occupies the only listener slot");
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
                "The player slot it vacated is free again");
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
                "A move changes no total, so the IP stays one tracked entry");
        });
    }

    [Test]
    public void Reclassify_Refused_MutatesNothing_AndPeerStaysReleasableAsPlayer()
    {
        // All-or-nothing: with the listener budget full the peer must keep the player slot it
        // already holds, so the ordinary Disconnected release still frees the right budget.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 1);
        var peer = new PeerIndex(12);

        Assume.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True,
            "Precondition: the only listener slot is taken by another connection");
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);

        Assert.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.False);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
            "The refused peer still holds its player slot — nothing was moved out");

        harness.Limiter.Release(peer);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
                "Release frees the player slot the peer actually held");
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False,
                "The refused move never charged the listener budget");
        });
    }

    [Test]
    public void Release_AfterReclassify_FreesListenerCapacity_NotPlayerCapacity()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 1);
        var peer = new PeerIndex(13);

        Assume.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);
        Assume.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True);

        harness.Limiter.Release(peer);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True,
                "The listener slot the peer held was freed");
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
                "The player budget was already vacated by the move…");
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
                "…and was not credited a second time by the release");
        });
    }

    [Test]
    public void Reclassify_PeerHoldingNoReservation_Succeeds()
    {
        // A peer index that was never bound — never acquired, or already released. There is nothing
        // indexed for it, so there is nothing to move, and refusing on missing bookkeeping would
        // disconnect a peer the limiter is holding no budget for.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 1);

        Assert.That(harness.Limiter.TryReclassify(new PeerIndex(999), ConnectionClass.SCENE_LISTENER), Is.True);
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0));
    }

    [Test]
    public void Reclassify_PeerWithUnattributableIp_Succeeds_AndCreatesNoEntry()
    {
        // The other half of that story, and the branch that actually carries it: a peer whose
        // address the transport could not render *does* hold a reservation — the transports call
        // Bind unconditionally — but it is keyed on the empty string and no per-IP count was ever
        // taken for it. The promotion must find nothing to move rather than conjure an entry under
        // the empty key, which every IP with an unreadable address would then share.
        using Harness harness = Create(enabled: false, maxConcurrency: 1, sceneListenerMaxConcurrency: 1);
        var peer = new PeerIndex(17);

        Assume.That(harness.Limiter.TryAcquire(string.Empty, ConnectionClass.PLAYER), Is.True,
            "A disabled limiter admits an unattributable peer and counts nothing for it");
        harness.Limiter.Bind(peer, string.Empty, ConnectionClass.PLAYER);
        Assume.That(harness.Limiter.TrackedIps, Is.EqualTo(0), "Precondition: the peer is bound but uncounted");

        harness.Reconfigure(o => o.Enabled = true);

        Assert.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0),
                "No empty-string entry was created by the move");
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True,
                "…and no real IP was charged for it either");
        });

        harness.Limiter.Release(peer);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "Releasing the uncounted peer decrements nothing — only the real IP above is tracked");
    }

    [Test]
    public void Reclassify_ToTheClassAlreadyHeld_IsANoOp_EvenAtCap()
    {
        // The peer is already in the target class, so the move is a no-op — including when that
        // class is full, where treating the peer's own slot as somebody else's would refuse a
        // promotion it has already been granted and record a refusal that never happened.
        using Harness harness = Create(enabled: true, maxConcurrency: 2, sceneListenerMaxConcurrency: 1);
        var peer = new PeerIndex(16);

        Assume.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);
        Assume.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True,
            "Precondition: the peer holds the only listener slot");

        List<ConnectionClass> refused = CaptureRefusedClasses(() =>
            Assert.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True,
                "A repeat promotion to the class already held is idempotent"));

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.Empty, "…and records no refusal");
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
                "Both player slots are free — the first move vacated one and the repeat moved nothing");
        });

        harness.Limiter.Release(peer);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True,
            "One release frees the one listener slot the peer held — the repeat charged no second one");
    }

    [Test]
    public void Reclassify_SourceClassHoldsNoCount_MovesNothing()
    {
        // Constructed drift, not a live sequence: no call order reaches this today (Abandon only
        // runs before Bind, and the allocator will not reissue a PeerIndex before Release). It is
        // guarded anyway because this is the only method that mutates two classes at once, so it is
        // the only place a future call-site mistake would decrement below zero — which widens the
        // source class's cap instead of failing.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 2);
        var peer = new PeerIndex(15);

        Assume.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
        harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);
        Assume.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True,
            "Keeps the IP's entry alive once its player count goes to zero");

        // Surplus credit: the peer's reservation still names PLAYER, but PLAYER now holds no count.
        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);

        Assert.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.False,
                "A decrement to -1 would have widened the player cap to two");
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False,
                "The listener budget was not charged either — the move was declined whole");
        });
    }

    [Test]
    public void DisabledLimiter_StillReclassifies_SoEnablingChargesTheListenerBudget()
    {
        // Documented semantic: Enabled gates the refusal, never the move. If promotion paused while
        // disabled, these five listeners would still sit in the player budget when the limiter came
        // on — the IP would have five player slots instead of ten, and the refusals would be
        // labelled class="player", sending an operator to MaxConcurrency for a listener problem.
        const int LISTENERS = 5;

        using Harness harness = Create(enabled: false, maxConcurrency: 10, sceneListenerMaxConcurrency: 2);

        for (var i = 0; i < LISTENERS; i++)
        {
            var peer = new PeerIndex((uint)(20 + i));

            Assume.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True);
            harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);
            Assume.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True,
                "A disabled limiter refuses nothing, so every promotion is granted");
        }

        harness.Reconfigure(o => o.Enabled = true);

        List<ConnectionClass> refused = CaptureRefusedClasses(() =>
        {
            for (var i = 0; i < 10; i++)
                Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER), Is.True,
                    "The five moves vacated the player budget, so its whole cap is available to players");

            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False,
                "The five listeners are on the listener books, already over its cap of two");
        });

        Assert.That(refused, Is.EqualTo(new[] { ConnectionClass.SCENE_LISTENER }),
            "Only the listener budget refuses — a player-class refusal here would name the wrong knob");
    }

    [Test]
    public void Whitelist_ExemptsTheListenerCapToo()
    {
        // One exemption list covers every budget — an operator whitelisting a fleet's egress IP
        // should not have to discover a second list.
        using Harness harness = Create(enabled: true, maxConcurrency: 1,
            sceneListenerMaxConcurrency: 1, whitelist: IP);
        var peer = new PeerIndex(14);

        for (var i = 0; i < 10; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);

        Assert.That(harness.Limiter.TryAcquire(OTHER_IP, ConnectionClass.SCENE_LISTENER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(OTHER_IP, ConnectionClass.SCENE_LISTENER), Is.False,
            "The whitelist exempts only the listed IP");

        harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER);
        harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);

        List<long> bypasses = CaptureMeasurements("pulse.hardening.ip_limit_whitelist_bypass", () =>
            Assert.That(harness.Limiter.TryReclassify(peer, ConnectionClass.SCENE_LISTENER), Is.True,
                "A promotion onto a whitelisted IP is exempt from the listener cap as well"));

        Assert.That(bypasses, Is.EqualTo(new[] { 1L }),
            "…and the exemption is on the record: the bypass counter is the only signal that separates "
            + "a load-bearing whitelist entry from a vestigial one, on this path as much as on connect");
    }

    [Test]
    public void ZeroSceneListenerMaxConcurrency_DisablesEnforcement_ButStillCounts()
    {
        // Zero means unlimited, not "no listeners allowed" — and counting continues, so raising the
        // cap later enforces against the live population rather than a zero baseline.
        using Harness harness = Create(enabled: true, maxConcurrency: 1, sceneListenerMaxConcurrency: 0);

        for (var i = 0; i < 100; i++)
            Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);

        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1));

        harness.Reconfigure(o => o.SceneListenerMaxConcurrency = 2);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False,
            "The hundred existing listener connections must already be on the books");
    }

    [Test]
    public void SceneListenerMaxConcurrency_ChangedAtRuntime_TakesEffect()
    {
        using Harness harness = Create(enabled: true, maxConcurrency: 10, sceneListenerMaxConcurrency: 1);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False);

        harness.Reconfigure(o => o.SceneListenerMaxConcurrency = 3);

        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True,
            "The cap is read per call, so a live raise takes effect on the next acquire");
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False,
            "…up to the new cap, and no further");
    }

    [Test]
    public void PerIpEntry_RemovedOnlyWhenEveryClassIsZero()
    {
        // The entry is the tracked-IP unit, shared by both budgets. Dropping it when one class hits
        // zero would strand the other class's live count and let a surplus release underflow it.
        using Harness harness = Create(enabled: true, maxConcurrency: 2, sceneListenerMaxConcurrency: 2);

        harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER);
        harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER);
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1));

        harness.Limiter.Abandon(IP, ConnectionClass.PLAYER);
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(1),
            "The player class is empty but the listener class still holds a reservation");

        harness.Limiter.Abandon(IP, ConnectionClass.SCENE_LISTENER);
        Assert.That(harness.Limiter.TrackedIps, Is.EqualTo(0),
            "Only with every class at zero is the entry removed");

        // A stale entry would have kept the listener count and decremented it to -1 above, widening
        // the cap; a re-acquired IP must see the full cap again in both classes.
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.True);
        Assert.That(harness.Limiter.TryAcquire(IP, ConnectionClass.SCENE_LISTENER), Is.False);
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
                if (harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER))
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

                harness.Limiter.TryAcquire(IP, ConnectionClass.PLAYER);
                harness.Limiter.Bind(peer, IP, ConnectionClass.PLAYER);
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

    private static Harness Create(bool enabled, int maxConcurrency,
        int sceneListenerMaxConcurrency = 0, string whitelist = "") =>
        new (enabled, maxConcurrency, sceneListenerMaxConcurrency, whitelist);

    /// <summary>
    ///     Values recorded on <paramref name="instrumentName" /> by <paramref name="action" />. The
    ///     instruments are process-global, so the listener is scoped to the call under test:
    ///     <c>Counter.Add</c> delivers synchronously, which makes the returned list that call's
    ///     measurements rather than a process total.
    /// </summary>
    private static List<long> CaptureMeasurements(string instrumentName, Action action)
    {
        var measurements = new List<long>();

        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => measurements.Add(measurement));
        listener.Start();

        action();

        return measurements;
    }

    /// <summary>
    ///     The connection class named by each refusal <paramref name="action" /> records — the label
    ///     an operator reads off <c>ip_limit_refused</c> to decide which cap to look at, so a
    ///     refusal filed under the wrong class is as much a defect as a missing one.
    /// </summary>
    private static List<ConnectionClass> CaptureRefusedClasses(Action action)
    {
        var refused = new List<ConnectionClass>();

        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "pulse.hardening.ip_limit_refused")
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Value is ConnectionClass connectionClass)
                    refused.Add(connectionClass);
        });

        listener.Start();

        action();

        return refused;
    }

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

        public Harness(bool enabled, int maxConcurrency, int sceneListenerMaxConcurrency, string whitelist)
        {
            options = new IpLimiterOptions
            {
                Enabled = enabled,
                MaxConcurrency = maxConcurrency,
                SceneListenerMaxConcurrency = sceneListenerMaxConcurrency,
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
