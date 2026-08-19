using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Core;
using Pulse.FeatureFlags;

namespace DCLPulseTests.FeatureFlags;

/// <summary>
///     Projection of a remote feature-flag document onto configuration keys. Unleash is a trusted
///     source, so any key it names is applied; <c>dynamicconfig.json</c> is a type schema, not a
///     filter. The defining property is that every failure is soft and as narrow as it can be: an
///     unbindable value costs only its own key, while a malformed payload or a consumer that refuses
///     the new document leaves the previously applied overrides running rather than blanking them.
/// </summary>
[TestFixture]
public class PulseFlagsConfigurationProviderTests
{
    /// <summary>
    ///     The case that motivated this fixture: the exact body the live document served, end to end
    ///     through the real checked-in schema.
    /// </summary>
    [Test]
    public void Apply_RealDocument_LandsEveryLeaf()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        provider.Apply(FeatureFlagsTestDoubles.RealDocument());

        Assert.Multiple(() =>
        {
            // Microsoft's JSON configuration parser stringifies booleans as "True"/"False"; the options
            // binder parses that back case-insensitively.
            Assert.That(Get(provider, FeatureFlagsTestDoubles.ENABLED_KEY), Is.EqualTo("True"));
            Assert.That(Get(provider, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo("10"));
            Assert.That(Get(provider, FeatureFlagsTestDoubles.WHITELIST_KEY), Is.Empty);
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public void Apply_NestedFragment_FlattensToLeafConfigurationKeys()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        Assert.Multiple(() =>
        {
            Assert.That(provider.AppliedOverrides.Keys, Is.EquivalentTo(new[]
            {
                FeatureFlagsTestDoubles.ENABLED_KEY,
                FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY,
                FeatureFlagsTestDoubles.WHITELIST_KEY,
            }));

            Assert.That(Get(provider, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo("25"));
            Assert.That(Get(provider, FeatureFlagsTestDoubles.WHITELIST_KEY), Is.EqualTo("10.0.0.1"));

            // Section nodes are not overridable knobs and must not be published as empty keys.
            Assert.That(provider.TryGet("Transport:Hardening:IpLimiter", out _), Is.False);
            Assert.That(provider.TryGet("Transport", out _), Is.False);
        });
    }

    /// <summary>
    ///     Unleash is a trusted source: a key <c>dynamicconfig.json</c> declares no default for is
    ///     applied like any other. There is no type to hold it to — that is the only thing its
    ///     absence from the file costs it.
    /// </summary>
    [Test]
    public void Apply_KeyWithNoDeclaredDefault_IsAppliedUnchecked()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("peers", FeatureFlagsTestDoubles.UNDECLARED_KEY_FRAGMENT));

        Assert.Multiple(() =>
        {
            Assert.That(Get(provider, FeatureFlagsTestDoubles.UNDECLARED_KEY), Is.EqualTo("True"));
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    ///     A flag with no <c>configuration</c> variant is an ordinary feature flag, not a configuration
    ///     fragment: it contributes no keys at all rather than being mapped onto one by convention.
    /// </summary>
    [Test]
    public void Apply_FlagWithoutAConfigurationVariant_ContributesNothing()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        provider.Apply(new FeatureFlagsDocument
        {
            Flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["transport-hardening"] = true,
                ["peers-resync-with-delta"] = true,
            },
        });

        Assert.That(provider.AppliedOverrides, Is.Empty);
    }

    /// <summary>
    ///     A flag flipped to <c>false</c> is the per-subsystem kill switch: its fragment is ignored and
    ///     the keys it previously set are withdrawn, restoring the shipped defaults.
    /// </summary>
    [Test]
    public void Apply_FlagTurnedFalse_IgnoresItsFragmentAndClearsPreviousOverrides()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        Assume.That(provider.AppliedOverrides, Has.Count.EqualTo(3));

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT, enabled: false));

        Assert.Multiple(() =>
        {
            Assert.That(provider.AppliedOverrides, Is.Empty);
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY, out _), Is.False);
        });
    }

    /// <summary>
    ///     A syntax error in the Unleash payload string must not blank the configuration mid-flight —
    ///     the last good document keeps running and the failure is logged.
    /// </summary>
    [Test]
    public void Apply_MalformedPayloadValue_RetainsPreviousOverrides()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", "{\"Transport\": {"));

        Assert.Multiple(() =>
        {
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(3));
            Assert.That(Get(provider, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo("25"));
            Assert.That(LoggedWarning(logger, "malformed configuration payload"), Is.True);
        });
    }

    // ---- Layer 1: a value must be convertible to the type dynamicconfig.json declares for its key.

    /// <summary>
    ///     <c>"MaxConcurrency": "ten"</c> — a key whose declared type is known, holding a value the
    ///     binder cannot convert to <c>int</c>. Only that key is skipped: its siblings in the same
    ///     fragment are perfectly good knobs, and withholding them would make one typo cost the whole
    ///     operator change. The skipped key falls back to what the lower-precedence sources give it.
    /// </summary>
    [Test]
    public void Apply_ValueThatCannotBindToItsDeclaredType_SkipsOnlyThatKey()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.POISON_FRAGMENT));

        Assert.Multiple(() =>
        {
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY, out _), Is.False,
                "the unbindable key must not reach Data, not even with its previous value");
            Assert.That(Get(provider, FeatureFlagsTestDoubles.ENABLED_KEY), Is.EqualTo("True"));
            Assert.That(Get(provider, FeatureFlagsTestDoubles.WHITELIST_KEY), Is.Empty);
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(2));
            Assert.That(LoggedWarning(logger, "MaxConcurrency", "ten", "integer"), Is.True,
                "the warning must name the offending key, its value and the expected type");
        });
    }

    /// <summary>
    ///     <c>"Whitelist": ["10.0.0.1","10.0.0.2"]</c> — a list written where the schema declares a
    ///     delimited string. Configuration flattens it into <c>Whitelist:0</c> and <c>Whitelist:1</c>,
    ///     which no option binds to, while <c>Whitelist</c> itself never appears and keeps its shipped
    ///     default. Left unchecked the document reads as applied while the knob it meant to set stays
    ///     empty, so the indexed keys are skipped and the shape is named in the warning.
    /// </summary>
    [Test]
    public void Apply_ArrayWhereTheSchemaDeclaresAScalar_SkipsTheIndexedKeysAndWarns()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ARRAY_WHITELIST_FRAGMENT));

        Assert.Multiple(() =>
        {
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.WHITELIST_INDEX_0_KEY, out _), Is.False);
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.WHITELIST_INDEX_1_KEY, out _), Is.False);
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.WHITELIST_KEY, out _), Is.False,
                "the array never sets the scalar key, so it must fall back to the shipped default");
            Assert.That(Get(provider, FeatureFlagsTestDoubles.ENABLED_KEY), Is.EqualTo("True"),
                "the skip is per key: the rest of the fragment still applies");
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(1));
            Assert.That(
                LoggedWarning(logger, FeatureFlagsTestDoubles.WHITELIST_INDEX_0_KEY, "comma-separated"), Is.True,
                "the warning must name the offending key and the shape a list-shaped knob takes");
        });
    }

    /// <summary>
    ///     The skip is scoped to the key, not to the flag that carried it: one subsystem's typo must
    ///     not withhold another subsystem's configuration from the same document.
    /// </summary>
    [Test]
    public void Apply_UnbindableKeyInOneFlag_LeavesAnotherFlagsKeysIntact()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        provider.Apply(new FeatureFlagsDocument
        {
            Flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["hardening"] = true,
                ["peers"] = true,
            },
            Variants = new Dictionary<string, FeatureFlagVariant>(StringComparer.OrdinalIgnoreCase)
            {
                ["hardening"] = Variant(FeatureFlagsTestDoubles.ONLY_POISON_FRAGMENT),
                ["peers"] = Variant(FeatureFlagsTestDoubles.UNDECLARED_KEY_FRAGMENT),
            },
        });

        Assert.Multiple(() =>
        {
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY, out _), Is.False);
            Assert.That(Get(provider, FeatureFlagsTestDoubles.UNDECLARED_KEY), Is.EqualTo("True"));
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    ///     The degenerate case: skipping the only key the document sets leaves an empty document,
    ///     which must apply as an empty document rather than fault anything. This is the shape that
    ///     used to take the host down before the type check existed.
    /// </summary>
    [Test]
    public void Apply_DocumentWhoseOnlySettingIsUnbindable_AppliesNothingAndDoesNotThrow()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        Assert.DoesNotThrow(() =>
            provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ONLY_POISON_FRAGMENT)));

        Assert.Multiple(() =>
        {
            Assert.That(provider.AppliedOverrides, Is.Empty);
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY, out _), Is.False);
        });
    }

    /// <summary>
    ///     Convertibility, not kind identity. A quoted number is what the binder sees anyway — every
    ///     configuration value is a string by the time it reaches it — so rejecting it would refuse a
    ///     document that works.
    /// </summary>
    [Test]
    public void Apply_QuotedNumberForANumericKnob_IsAccepted()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.QUOTED_NUMBER_FRAGMENT));

        Assert.That(Get(provider, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo("20"));
    }

    /// <summary>
    ///     The dangerous variant: the poison arrives on the blocking first load, before
    ///     <c>IOptionsMonitor</c> exists, so nothing throws at boot. Left unchecked the server starts
    ///     clean, passes health checks and dies on the first read of the knob. The check must leave
    ///     that one knob on its shipped default while the rest of the document still boots.
    /// </summary>
    [Test]
    public void Load_UnbindableValueOnTheBlockingFirstFetch_LeavesThatKnobOnItsShippedDefault()
    {
        using var endpoint = new StubFlagsEndpoint(
            FeatureFlagsTestDoubles.DocumentBody(FeatureFlagsTestDoubles.POISON_FRAGMENT));

        using FeatureFlagsClient client = endpoint.Client();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(client: client);

        provider.Load();

        Assert.Multiple(() =>
        {
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY, out _), Is.False);
            Assert.That(Get(provider, FeatureFlagsTestDoubles.ENABLED_KEY), Is.EqualTo("True"));
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(2));
        });
    }

    /// <summary>
    ///     End to end through a real <c>IConfigurationRoot</c> and a real <c>IOptionsMonitor</c>: the
    ///     unbindable value must never become readable, and skipping its key must hand the read back
    ///     to the lower-precedence source. Without the type check this is the reproduction —
    ///     <c>CurrentValue</c> throws <c>InvalidOperationException</c> on the first read, which in the
    ///     server is a transport thread and therefore a stopped host.
    /// </summary>
    [Test]
    public void Apply_UnbindableValue_NeverReachesTheOptionsBinder()
    {
        PulseFlagsConfigurationSource source = FeatureFlagsTestDoubles.Source();

        IConfigurationRoot configuration = new ConfigurationBuilder()
                                           .AddInMemoryCollection(new Dictionary<string, string?>
                                           {
                                               [FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY] = "10",
                                           })
                                           .Add(source)
                                           .Build();

        var services = new ServiceCollection();
        services.Configure<LimiterOptionsStub>(configuration.GetSection(FeatureFlagsTestDoubles.IP_LIMITER_SECTION));

        using ServiceProvider container = services.BuildServiceProvider();
        var monitor = container.GetRequiredService<IOptionsMonitor<LimiterOptionsStub>>();

        Assume.That(monitor.CurrentValue.MaxConcurrency, Is.EqualTo(10));

        source.Provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.POISON_FRAGMENT));

        Assert.That(monitor.CurrentValue.MaxConcurrency, Is.EqualTo(10));
    }

    // ---- Layer 2: a swap that a consumer refuses must roll back rather than escape.

    /// <summary>
    ///     Type validation cannot foresee every way a binder can refuse a value, and the throw would
    ///     otherwise escape <c>Apply</c> — miscounted as a transport failure by the poller, or, on the
    ///     blocking first load, straight out of startup. The swap unwinds instead: previous overrides
    ///     restored, consumers notified again, the rejection logged, nothing propagated.
    /// </summary>
    [Test]
    public void Apply_ConsumerThrowsWhileRebinding_RollsBackAndDoesNotPropagate()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        provider.GetReloadToken()
                .RegisterChangeCallback(_ => throw new InvalidOperationException("binder refused the value"), state: null);

        Assert.DoesNotThrow(() => provider.Apply(FeatureFlagsTestDoubles.RealDocument()));

        Assert.Multiple(() =>
        {
            Assert.That(Get(provider, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo("25"));
            Assert.That(Get(provider, FeatureFlagsTestDoubles.WHITELIST_KEY), Is.EqualTo("10.0.0.1"));
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(3));
            Assert.That(LoggedWarning(logger, "rolled back to the previous overrides"), Is.True);
        });
    }

    /// <summary>
    ///     The rollback fires the reload token a second time, so a consumer that already rebound to the
    ///     rejected document is told to come back off it. Without it the provider's data and the
    ///     consumer's cached options disagree until the next poll.
    /// </summary>
    [Test]
    public void Apply_ConsumerThrowsWhileRebinding_NotifiesConsumersOfTheRollback()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        var unwound = false;
        provider.GetReloadToken().RegisterChangeCallback(_ => throw new InvalidOperationException("binder refused"), state: null);

        // Registered on the token the failing swap replaces, so it only fires on the rollback reload.
        ConfigureRollbackProbe(provider, () => unwound = true);

        provider.Apply(FeatureFlagsTestDoubles.RealDocument());

        Assert.That(unwound, Is.True);
    }

    /// <summary>
    ///     Consumers see a new document through the reload token they already subscribe to via
    ///     <c>IOptionsMonitor</c>; without it an applied override would sit in <c>Data</c> unread.
    /// </summary>
    [Test]
    public void Apply_AcceptedDocument_FiresTheReloadToken()
    {
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider();
        var reloaded = false;
        provider.GetReloadToken().RegisterChangeCallback(_ => reloaded = true, state: null);

        provider.Apply(FeatureFlagsTestDoubles.RealDocument());

        Assert.That(reloaded, Is.True);
    }

    // ---- Layer 3: an applied document announces itself exactly once.

    /// <summary>
    ///     The success path has no metric behind it, so this log is the only positive confirmation
    ///     that a document was fetched, accepted and is now in force. It names the resulting key count
    ///     so an operator can tell "applied, three knobs" from "applied, nothing".
    /// </summary>
    [Test]
    public void Apply_OverrideSetChanged_LogsTheResultingKeyCount()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);

        provider.Apply(FeatureFlagsTestDoubles.RealDocument());

        Assert.That(LoggedInformation(logger, "overrides changed", "3", FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.True);
    }

    /// <summary>
    ///     The poller refetches on a fixed interval and the document rarely changes. Logging every
    ///     successful poll would bury the one line that matters, so a document identical to the one
    ///     already applied says nothing.
    /// </summary>
    [Test]
    public void Apply_SameDocumentAgain_LogsNothing()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.RealDocument());
        logger.ClearReceivedCalls();

        provider.Apply(FeatureFlagsTestDoubles.RealDocument());

        Assert.That(LoggedInformation(logger, "overrides changed"), Is.False);
    }

    /// <summary>A changed value on the same key is a change, even though the key set is identical.</summary>
    [Test]
    public void Apply_SameKeysWithADifferentValue_LogsAgain()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.RealDocument());
        logger.ClearReceivedCalls();

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        Assert.That(LoggedInformation(logger, "overrides changed", "3"), Is.True);
    }

    /// <summary>
    ///     A rolled-back swap is not an applied document. Announcing it would tell an operator the
    ///     server is running overrides it rejected.
    /// </summary>
    [Test]
    public void Apply_ConsumerThrowsWhileRebinding_DoesNotLogAsApplied()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.GetReloadToken()
                .RegisterChangeCallback(_ => throw new InvalidOperationException("binder refused"), state: null);

        provider.Apply(FeatureFlagsTestDoubles.RealDocument());

        Assert.That(LoggedInformation(logger, "overrides changed"), Is.False);
    }

    /// <summary>
    ///     <c>"Whitelist": [{"ip":"10.0.0.1"}]</c> — the same array-for-a-scalar mistake, one level
    ///     deeper. It flattens to <c>Whitelist:0:ip</c>, whose immediate parent <c>Whitelist:0</c> is
    ///     undeclared, so a shape check that inspects only the parent calls it well-shaped and applies
    ///     it while <c>Whitelist</c> itself is never set and keeps its shipped default. That is
    ///     exactly the "reads as applied, knob never moves" outcome the shape check exists to close,
    ///     so nesting depth must not be a way around it.
    /// </summary>
    [Test]
    public void Apply_ObjectInsideAnArrayWhereTheSchemaDeclaresAScalar_SkipsTheNestedKeyAndWarns()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.NESTED_ARRAY_WHITELIST_FRAGMENT));

        Assert.Multiple(() =>
        {
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.WHITELIST_NESTED_IP_KEY, out _), Is.False);
            Assert.That(provider.TryGet(FeatureFlagsTestDoubles.WHITELIST_KEY, out _), Is.False,
                "the nested array never sets the scalar key, so it must fall back to the shipped default");
            Assert.That(Get(provider, FeatureFlagsTestDoubles.ENABLED_KEY), Is.EqualTo("True"),
                "the skip is per key: the rest of the fragment still applies");
            Assert.That(provider.AppliedOverrides, Has.Count.EqualTo(1));
            Assert.That(
                LoggedWarning(logger, FeatureFlagsTestDoubles.WHITELIST_NESTED_IP_KEY, FeatureFlagsTestDoubles.WHITELIST_KEY),
                Is.True,
                "the warning must name the nested key and the scalar knob it contradicts");
        });
    }

    // ---- Layer 4: a skipped key announces itself once per time it becomes bad, like the success line.

    /// <summary>
    ///     The poller refetches on a fixed interval, so a document that stays broken is re-checked
    ///     forever. Warning on every pass would be one line per bad key per interval — one per array
    ///     element, at that — and would undo the property the change-only Information line
    ///     establishes: a steady-state poller is silent.
    /// </summary>
    [Test]
    public void Apply_SameMisshapedDocumentTwice_WarnsOnlyOnce()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ARRAY_WHITELIST_FRAGMENT));
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ARRAY_WHITELIST_FRAGMENT));

        Assert.That(WarningCount(logger, FeatureFlagsTestDoubles.WHITELIST_INDEX_0_KEY), Is.EqualTo(1));
    }

    /// <summary>Same property for the type check, which shares the remembered skip set.</summary>
    [Test]
    public void Apply_SameUnbindableValueTwice_WarnsOnlyOnce()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.POISON_FRAGMENT));
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.POISON_FRAGMENT));

        Assert.That(WarningCount(logger, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo(1));
    }

    /// <summary>
    ///     Suppression is per key, not per document: a key that breaks for the first time must be
    ///     announced even when the same document also carries a key that was already broken.
    ///     Anything coarser hides the operator's newest mistake behind their oldest.
    /// </summary>
    [Test]
    public void Apply_NewlyBrokenKeyAlongsideAnAlreadySkippedOne_WarnsAboutTheNewOne()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ARRAY_WHITELIST_FRAGMENT));

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ARRAY_WHITELIST_AND_POISON_FRAGMENT));

        Assert.Multiple(() =>
        {
            Assert.That(WarningCount(logger, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo(1),
                "the newly unbindable key is news and must be warned about");
            Assert.That(WarningCount(logger, FeatureFlagsTestDoubles.WHITELIST_INDEX_0_KEY), Is.EqualTo(1),
                "the key already skipped by the previous apply must stay quiet");
        });
    }

    /// <summary>
    ///     The remembered set is the previous apply's, not everything ever seen: a key that is fixed
    ///     and later breaks again is a new problem and must be announced again.
    /// </summary>
    [Test]
    public void Apply_KeyFixedThenBrokenAgain_WarnsAgain()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.POISON_FRAGMENT));
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.FRAGMENT));

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.POISON_FRAGMENT));

        Assert.That(WarningCount(logger, FeatureFlagsTestDoubles.MAX_CONCURRENCY_KEY), Is.EqualTo(2));
    }

    /// <summary>
    ///     A payload that failed to parse establishes nothing about any key, so it must not read as
    ///     "those keys are fine now" and re-arm a warning the operator has already seen.
    /// </summary>
    [Test]
    public void Apply_MalformedPayloadBetweenTwoMisshapedDocuments_DoesNotReArmTheWarning()
    {
        var logger = Substitute.For<ILogger>();
        PulseFlagsConfigurationProvider provider = FeatureFlagsTestDoubles.Provider(logger: logger);
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ARRAY_WHITELIST_FRAGMENT));

        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", "{\"Transport\": {"));
        provider.Apply(FeatureFlagsTestDoubles.WithFragment("hardening", FeatureFlagsTestDoubles.ARRAY_WHITELIST_FRAGMENT));

        Assert.That(WarningCount(logger, FeatureFlagsTestDoubles.WHITELIST_INDEX_0_KEY), Is.EqualTo(1));
    }

    /// <summary>
    ///     Subscribes <paramref name="onRollback" /> to whichever token the provider publishes next, by
    ///     chaining off the current one — the failing swap replaces the token before it throws, so the
    ///     rollback reload fires the successor.
    /// </summary>
    private static void ConfigureRollbackProbe(PulseFlagsConfigurationProvider provider, Action onRollback)
    {
        provider.GetReloadToken()
                .RegisterChangeCallback(_ => provider.GetReloadToken().RegisterChangeCallback(_ => onRollback(), state: null),
                    state: null);
    }

    /// <summary>An enabled <c>configuration</c> variant carrying <paramref name="fragmentJson" />.</summary>
    private static FeatureFlagVariant Variant(string fragmentJson) =>
        new ()
        {
            Name = "configuration",
            Enabled = true,
            Payload = new FeatureFlagVariantPayload { Type = "json", Value = fragmentJson },
        };

    private static string? Get(PulseFlagsConfigurationProvider provider, string key)
    {
        Assert.That(provider.TryGet(key, out string? value), Is.True, $"{key} must be present in Data");
        return value;
    }

    /// <summary>Whether a warning was logged whose rendered message contains every fragment given.</summary>
    private static bool LoggedWarning(ILogger logger, params string[] fragments) =>
        Logged(logger, LogLevel.Warning, fragments) > 0;

    /// <summary>Whether an information line was logged whose rendered message contains every fragment given.</summary>
    private static bool LoggedInformation(ILogger logger, params string[] fragments) =>
        Logged(logger, LogLevel.Information, fragments) > 0;

    /// <summary>
    ///     How many warnings were logged whose rendered message contains every fragment given. The
    ///     count, not just its presence, is the assertion for a warning that must not repeat.
    /// </summary>
    private static int WarningCount(ILogger logger, params string[] fragments) =>
        Logged(logger, LogLevel.Warning, fragments);

    private static int Logged(ILogger logger, LogLevel level, string[] fragments)
    {
        var matches = 0;

        foreach (ICall call in logger.ReceivedCalls())
        {
            if (call.GetMethodInfo().Name != nameof(ILogger.Log))
                continue;

            object?[] arguments = call.GetArguments();

            if (arguments.Length < 3 || !Equals(arguments[0], level))
                continue;

            string message = arguments[2]?.ToString() ?? string.Empty;
            var matched = true;

            foreach (string fragment in fragments)
                matched &= message.Contains(fragment, StringComparison.Ordinal);

            if (matched)
                matches++;
        }

        return matches;
    }

    /// <summary>
    ///     Stand-in for a consumer of the dynamic knobs, shaped like the real limiter options. The
    ///     <c>int</c> property is what an unbindable override breaks against.
    /// </summary>
    private sealed class LimiterOptionsStub
    {
        public bool Enabled { get; set; }

        public int MaxConcurrency { get; set; } = 10;

        public string Whitelist { get; set; } = string.Empty;
    }
}
