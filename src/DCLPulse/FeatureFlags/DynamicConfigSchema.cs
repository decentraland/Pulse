using System.Globalization;
using System.Text.Json;

namespace Pulse.FeatureFlags;

/// <summary>
///     The type each remotely settable configuration key's value must convert to, read from the raw
///     JSON of <c>dynamicconfig.json</c>. A default value has a JSON type, so the file that carries
///     the offline defaults also declares their schema.
///     <para />
///     <b>Why this reads the file itself instead of asking <c>IConfiguration</c>.</b> The
///     configuration system stringifies every value as it loads: <c>10</c> becomes <c>"10"</c>, and
///     the JSON type is gone by the time a built <c>IConfiguration</c> can be queried. This raw parse
///     is the only place a key's declared type survives, and therefore the only thing that can tell
///     a value the options binder will refuse from one it will accept.
///     <para />
///     It is not a security boundary — the remote document may set any key. A key this file does not
///     declare simply has no known type and is applied unchecked.
///     <para />
///     Only scalar leaves are collected; a JSON array contributes no keys, matching the rule that
///     list-shaped knobs are delimited strings (see <c>docs/feature-flags.md</c>). A remote document
///     that writes one anyway is caught by <see cref="IsUnderScalarKey" />.
///     <para />
///     Read once at construction. <c>dynamicconfig.json</c> is registered <c>reloadOnChange</c>, so
///     editing an existing value on a running server takes effect immediately, but <em>adding</em>
///     a key does not make it type-checked until the process restarts.
/// </summary>
public sealed class DynamicConfigSchema
{
    /// <summary>File name of the dynamic-configuration defaults, relative to the host's content root.</summary>
    public const string FILE_NAME = "dynamicconfig.json";

    // Matches what .NET's JSON configuration parser tolerates, so the checked-in file's comments and
    // trailing commas parse here exactly as they do when it is loaded as configuration.
    private static readonly JsonDocumentOptions JSON_OPTIONS = new ()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, ExpectedType> expectedTypes;

    private DynamicConfigSchema(Dictionary<string, ExpectedType> expectedTypes)
    {
        this.expectedTypes = expectedTypes;
    }

    /// <summary>
    ///     Reads the schema file. Throws when it is missing, matching the <c>optional: false</c> the
    ///     same file is registered with as a configuration source — one reader failing loudly while
    ///     the other shrugged would be a disagreement about whether the shipped defaults exist. The
    ///     path is taken as given, rooted at the content root, so both readers read the same copy.
    /// </summary>
    public static DynamicConfigSchema LoadFromFile(string path) =>
        FromJson(File.ReadAllText(path));

    /// <summary>
    ///     Collects the scalar leaves of <paramref name="json" /> as <c>Section:Sub:Leaf</c>
    ///     configuration keys paired with the type of their default. Object nodes are recursed into
    ///     and never become keys: only a key that holds a value declares a type.
    /// </summary>
    public static DynamicConfigSchema FromJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, JSON_OPTIONS);

        var types = new Dictionary<string, ExpectedType>(StringComparer.OrdinalIgnoreCase);

        if (document.RootElement.ValueKind == JsonValueKind.Object)
            Collect(document.RootElement, prefix: null, types);

        return new DynamicConfigSchema(types);
    }

    /// <summary>
    ///     Whether <paramref name="value" /> could bind to the type <paramref name="configKey" />'s
    ///     shipped default declares, and so would survive the configuration binder. Convertibility,
    ///     not kind identity: the binder only ever sees strings, so a quoted number binds to an
    ///     <c>int</c> exactly as a bare one does, while <c>"ten"</c> does not.
    ///     <paramref name="expectedType" /> names the declared type for a rejection log.
    ///     <para />
    ///     A key this schema does not declare accepts anything — there is no default to read a type
    ///     off, so nothing can be checked.
    /// </summary>
    public bool Accepts(string configKey, string? value, out string expectedType)
    {
        if (!expectedTypes.TryGetValue(configKey, out ExpectedType expected))
        {
            expectedType = string.Empty;
            return true;
        }

        expectedType = expected.Name;

        return expected.Accepts(value);
    }

    /// <summary>
    ///     Whether any ancestor path of <paramref name="configKey" /> is itself a declared scalar key,
    ///     and if so names the nearest such ancestor in <paramref name="scalarKey" />. Only a value
    ///     declares a type here, so a declared key is a leaf and can have no descendants: a key nested
    ///     under one is a JSON array or object written where a scalar belongs — <c>Whitelist:0</c>
    ///     under a string <c>Whitelist</c> — which the configuration flattener produces and no option
    ///     binds to.
    ///     <para />
    ///     Every ancestor is walked, not just the immediate parent, because the offending value can
    ///     nest arbitrarily deep: <c>"Whitelist": [{"ip": "10.0.0.1"}]</c> flattens to
    ///     <c>Whitelist:0:ip</c>, whose parent <c>Whitelist:0</c> is undeclared while
    ///     <c>Whitelist</c> two levels up is the scalar being contradicted. Prefixes are cut at
    ///     <c>:</c> boundaries only, so a declared <c>A:B</c> covers <c>A:B:0:ip</c> and not
    ///     <c>A:BC:x</c>.
    /// </summary>
    public bool IsUnderScalarKey(string configKey, out string scalarKey)
    {
        // Walks inward from the nearest ancestor, so the innermost declared scalar is the one named.
        for (int separator = configKey.LastIndexOf(':'); separator > 0; separator = configKey.LastIndexOf(':', separator - 1))
        {
            string ancestor = configKey[..separator];

            if (expectedTypes.ContainsKey(ancestor))
            {
                scalarKey = ancestor;
                return true;
            }
        }

        scalarKey = string.Empty;
        return false;
    }

    private static void Collect(JsonElement node, string? prefix, Dictionary<string, ExpectedType> types)
    {
        foreach (JsonProperty property in node.EnumerateObject())
        {
            string key = prefix is null ? property.Name : $"{prefix}:{property.Name}";

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Collect(property.Value, key, types);
                continue;
            }

            if (ExpectedType.TryDescribe(property.Value, out ExpectedType expected))
                types[key] = expected;
        }
    }

    /// <summary>
    ///     The type one declared value must convert to, derived from the JSON kind of its shipped
    ///     default. Numbers carry whether that default was integral: a knob defaulted to <c>10</c>
    ///     binds to an integer type, where <c>"1.5"</c> throws exactly as <c>"ten"</c> does.
    /// </summary>
    private readonly record struct ExpectedType(JsonValueKind Kind, bool Integral)
    {
        /// <summary>Human-readable type name for a rejection log.</summary>
        public string Name =>
            Kind switch
            {
                JsonValueKind.True => "boolean",
                JsonValueKind.Number => Integral ? "integer" : "number",
                _ => "string",
            };

        /// <summary>
        ///     Derives the expected type from a default value; false for a kind that declares no
        ///     type (null, array, and the object case the caller already handled).
        /// </summary>
        public static bool TryDescribe(JsonElement element, out ExpectedType expected)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.True:
                case JsonValueKind.False:
                    // Both booleans collapse onto one kind; only the type matters, not the default.
                    expected = new ExpectedType(JsonValueKind.True, Integral: false);
                    return true;

                case JsonValueKind.Number:
                    expected = new ExpectedType(JsonValueKind.Number, element.TryGetInt64(out _));
                    return true;

                case JsonValueKind.String:
                    expected = new ExpectedType(JsonValueKind.String, Integral: false);
                    return true;

                default:
                    expected = default(ExpectedType);
                    return false;
            }
        }

        /// <summary>
        ///     Mirrors the configuration binder: invariant culture, either casing of a boolean.
        ///     Anything is a valid string, so a string-typed knob never rejects.
        /// </summary>
        public bool Accepts(string? value) =>
            Kind switch
            {
                JsonValueKind.True => bool.TryParse(value, out _),
                JsonValueKind.Number => Integral
                    ? long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                _ => true,
            };
    }
}
