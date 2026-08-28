using Pulse.FeatureFlags;

namespace DCLPulseTests.FeatureFlags;

/// <summary>
///     The type schema read out of <c>dynamicconfig.json</c>. It exists because
///     <c>IConfiguration</c> stringifies every value on load, so the raw JSON is the only place a
///     key's declared type survives — which makes the parse itself, not just its verdicts, worth
///     pinning. Its verdicts must match what the configuration binder would do: accept anything
///     convertible, refuse only what would throw.
/// </summary>
[TestFixture]
public class DynamicConfigSchemaTests
{
    /// <summary>
    ///     The checked-in file carries `//` comments and would tolerate trailing commas. It is read
    ///     twice — once as configuration, once here — and the two readers must not disagree about
    ///     whether it parses.
    /// </summary>
    [Test]
    public void FromJson_CommentsAndTrailingCommas_ParseAsTheConfigurationLoaderWould()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson(
            """
            // Leading comment, as the shipped file has.
            {
              "Transport": { "Hardening": { "IpLimiter": {
                "MaxConcurrency": 10, // trailing comment
              }}},
            }
            """);

        Assert.That(schema.Accepts("Transport:Hardening:IpLimiter:MaxConcurrency", "ten", out string expectedType), Is.False);
        Assert.That(expectedType, Is.EqualTo("integer"));
    }

    [Test]
    public void LoadFromFile_MissingFile_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"dynamicconfig-{Guid.NewGuid():N}.json");

        // The same file is registered optional: false as a configuration source, so a reader that
        // shrugged at its absence would disagree with the one that already fails startup.
        Assert.That(() => DynamicConfigSchema.LoadFromFile(missing), Throws.InstanceOf<IOException>());
    }

    /// <summary>
    ///     Section nodes hold no value, so they declare no type and never become keys of their own —
    ///     otherwise the object node would shadow the leaves nested under it.
    /// </summary>
    [Test]
    public void FromJson_SectionNode_DeclaresNoType()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Transport":{"MaxPeers":100}}""");

        Assert.Multiple(() =>
        {
            Assert.That(schema.Accepts("Transport", "anything at all", out _), Is.True);
            Assert.That(schema.Accepts("Transport:MaxPeers", "lots", out _), Is.False);
        });
    }

    /// <summary>
    ///     A null or array default names no scalar type. The value is applied unchecked rather than
    ///     held to a type nobody declared — the same treatment a key absent from the file gets.
    /// </summary>
    [TestCase("""{"Section":{"Knob":null}}""", TestName = "FromJson_NullDefault_DeclaresNoType")]
    [TestCase("""{"Section":{"Knob":[1,2]}}""", TestName = "FromJson_ArrayDefault_DeclaresNoType")]
    public void FromJson_NonScalarDefault_DeclaresNoType(string json)
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson(json);

        Assert.That(schema.Accepts("Section:Knob", "whatever", out _), Is.True);
    }

    [Test]
    public void Accepts_KeyWithNoDeclaredDefault_AcceptsAnything()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Known":1}}""");

        Assert.That(schema.Accepts("Section:Unknown", "ten", out string expectedType), Is.True);
        Assert.That(expectedType, Is.Empty);
    }

    /// <summary>
    ///     Mirrors the configuration binder rather than JSON kind identity: the binder only ever
    ///     sees strings, so every representation it converts must be accepted and only what it would
    ///     throw on refused.
    /// </summary>
    [TestCase("true", true, TestName = "Accepts_BooleanKnob_BareTrue")]
    [TestCase("True", true, TestName = "Accepts_BooleanKnob_BinderCasing")]
    [TestCase("yes", false, TestName = "Accepts_BooleanKnob_RejectsYes")]
    [TestCase("1", false, TestName = "Accepts_BooleanKnob_RejectsNumericTruth")]
    public void Accepts_BooleanKnob(string value, bool expected)
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Flag":false}}""");

        Assert.That(schema.Accepts("Section:Flag", value, out string expectedType), Is.EqualTo(expected));
        Assert.That(expectedType, Is.EqualTo("boolean"));
    }

    [TestCase("20", true, TestName = "Accepts_IntegerKnob_QuotedNumber")]
    [TestCase("-3", true, TestName = "Accepts_IntegerKnob_Negative")]
    [TestCase("1.5", false, TestName = "Accepts_IntegerKnob_RejectsFractional")]
    [TestCase("ten", false, TestName = "Accepts_IntegerKnob_RejectsWords")]
    [TestCase("", false, TestName = "Accepts_IntegerKnob_RejectsEmpty")]
    public void Accepts_IntegerKnob(string value, bool expected)
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Count":10}}""");

        Assert.That(schema.Accepts("Section:Count", value, out string expectedType), Is.EqualTo(expected));
        Assert.That(expectedType, Is.EqualTo("integer"));
    }

    /// <summary>
    ///     A fractional default declares the wider type, so a whole number still binds. No shipped
    ///     knob has one yet; this pins the branch before one does.
    /// </summary>
    [TestCase("1.5", true, TestName = "Accepts_NumberKnob_Fractional")]
    [TestCase("2", true, TestName = "Accepts_NumberKnob_WholeNumber")]
    [TestCase("half", false, TestName = "Accepts_NumberKnob_RejectsWords")]
    public void Accepts_NumberKnob(string value, bool expected)
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Ratio":0.5}}""");

        Assert.That(schema.Accepts("Section:Ratio", value, out string expectedType), Is.EqualTo(expected));
        Assert.That(expectedType, Is.EqualTo("number"));
    }

    /// <summary>Every configuration value is a string by the time the binder sees it, so a
    ///     string-typed knob has nothing to refuse.</summary>
    [Test]
    public void Accepts_StringKnob_AcceptsAnyValue()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Whitelist":""}}""");

        Assert.Multiple(() =>
        {
            Assert.That(schema.Accepts("Section:Whitelist", "10.0.0.1,10.0.0.2", out _), Is.True);
            Assert.That(schema.Accepts("Section:Whitelist", string.Empty, out _), Is.True);
        });
    }

    /// <summary>
    ///     A declared key holds a value, so it can have no children of its own. A key nested under one
    ///     is a JSON array or object written where a scalar belongs — the configuration flattener turns
    ///     <c>"Whitelist": ["a","b"]</c> into <c>Whitelist:0</c> and <c>Whitelist:1</c>, and leaves
    ///     <c>Whitelist</c> itself on its default. Reported so the caller can skip the indexed key.
    /// </summary>
    [Test]
    public void IsUnderScalarKey_IndexedChildOfDeclaredScalar_NamesTheScalarKey()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Whitelist":""}}""");

        Assert.That(schema.IsUnderScalarKey("Section:Whitelist:0", out string scalarKey), Is.True);
        Assert.That(scalarKey, Is.EqualTo("Section:Whitelist"));
    }

    /// <summary>
    ///     The immediate parent is not enough: an array of objects flattens one level deeper.
    ///     <c>"Whitelist": [{"ip":"10.0.0.1"}]</c> becomes <c>Whitelist:0:ip</c>, whose parent
    ///     <c>Whitelist:0</c> is undeclared — only the grandparent contradicts it. Checking one
    ///     ancestor lets exactly this shape through as well-shaped while <c>Whitelist</c> keeps its
    ///     shipped default, which is the failure the guard exists to prevent.
    /// </summary>
    [TestCase("Section:Whitelist:0:ip", TestName = "IsUnderScalarKey_ObjectInsideArrayOverDeclaredScalar_NamesTheScalarKey")]
    [TestCase("Section:Whitelist:0:1:ip", TestName = "IsUnderScalarKey_ArbitrarilyDeepNestingOverDeclaredScalar_NamesTheScalarKey")]
    public void IsUnderScalarKey_NestedDeeperThanOneLevel_NamesTheScalarKey(string configKey)
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Whitelist":""}}""");

        Assert.That(schema.IsUnderScalarKey(configKey, out string scalarKey), Is.True);
        Assert.That(scalarKey, Is.EqualTo("Section:Whitelist"));
    }

    /// <summary>
    ///     Ancestors are cut at <c>:</c> boundaries only. A declared <c>Section:White</c> must not
    ///     make <c>Section:Whitelist:0</c> suspect — the declared key is a textual prefix of it but
    ///     not an ancestor of it, and treating it as one would skip a knob nothing is wrong with.
    /// </summary>
    [Test]
    public void IsUnderScalarKey_DeclaredKeyIsOnlyATextualPrefix_ReportsFalse()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"White":""}}""");

        Assert.That(schema.IsUnderScalarKey("Section:Whitelist:0", out string scalarKey), Is.False);
        Assert.That(scalarKey, Is.Empty);
    }

    /// <summary>
    ///     The nearest declared ancestor is the honest report: it is the knob the document actually
    ///     contradicts, where an outer section node declares no type to contradict at all.
    /// </summary>
    [Test]
    public void IsUnderScalarKey_NestedUnderADeclaredScalar_NamesTheNearestOne()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Outer":{"Inner":{"Whitelist":""}}}""");

        Assert.That(schema.IsUnderScalarKey("Outer:Inner:Whitelist:0:ip", out string scalarKey), Is.True);
        Assert.That(scalarKey, Is.EqualTo("Outer:Inner:Whitelist"));
    }

    /// <summary>Configuration keys are case-insensitive here too.</summary>
    [Test]
    public void IsUnderScalarKey_IndexedChildInAnotherCasing_StillResolves()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Whitelist":""}}""");

        Assert.That(schema.IsUnderScalarKey("section:whitelist:1", out _), Is.True);
    }

    /// <summary>
    ///     Only a declared parent makes a key suspect. A section node declares no type, a top-level
    ///     key has no parent at all, and an undeclared parent leaves nothing to contradict.
    /// </summary>
    [TestCase("Section:Whitelist", TestName = "IsUnderScalarKey_DeclaredKeyItself_IsNotUnderOne")]
    [TestCase("Section:Other", TestName = "IsUnderScalarKey_SiblingOfDeclaredKey_IsNotUnderOne")]
    [TestCase("Section", TestName = "IsUnderScalarKey_SectionNode_IsNotUnderOne")]
    [TestCase("Whitelist", TestName = "IsUnderScalarKey_TopLevelKey_IsNotUnderOne")]
    [TestCase("Elsewhere:Undeclared:0", TestName = "IsUnderScalarKey_ChildOfUndeclaredKey_IsNotUnderOne")]
    [TestCase("Elsewhere:Undeclared:0:ip", TestName = "IsUnderScalarKey_DeeplyNestedUnderUndeclaredKey_IsNotUnderOne")]
    public void IsUnderScalarKey_NotAnIndexOfADeclaredScalar_ReportsFalse(string configKey)
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Whitelist":""}}""");

        Assert.That(schema.IsUnderScalarKey(configKey, out string scalarKey), Is.False);
        Assert.That(scalarKey, Is.Empty);
    }

    /// <summary>Configuration keys are case-insensitive, so the schema must not be stricter.</summary>
    [Test]
    public void Accepts_KeyInAnotherCasing_ResolvesTheSameDeclaredType()
    {
        DynamicConfigSchema schema = DynamicConfigSchema.FromJson("""{"Section":{"Count":10}}""");

        Assert.That(schema.Accepts("section:count", "ten", out _), Is.False);
    }
}
