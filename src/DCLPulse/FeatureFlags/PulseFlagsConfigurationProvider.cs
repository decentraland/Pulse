using System.Text;

namespace Pulse.FeatureFlags;

/// <summary>
///     Projects the remote feature-flag document onto configuration keys and republishes them
///     whenever a new document arrives, so <c>IOptionsMonitor&lt;T&gt;</c> consumers pick the change
///     up through the plumbing they already use.
///     <para />
///     A <c>true</c> flag carrying a <c>configuration</c> variant contributes that variant's JSON
///     payload, flattened into keys; a <c>false</c> flag or one without such a variant contributes
///     nothing, which makes the flag a per-subsystem kill switch back to the shipped defaults. The
///     remote document may set any key; a value that fails the type or shape check against
///     <see cref="DynamicConfigSchema" /> — one that cannot convert to the type declared for its
///     key, or that nests under a key declared scalar — is logged and skipped, leaving the rest of
///     the document to apply.
/// </summary>
public sealed class PulseFlagsConfigurationProvider(
    FeatureFlagsOptions options,
    DynamicConfigSchema schema,
    FeatureFlagsClient client,
    ILogger bootstrapLogger)
    : ConfigurationProvider
{
    private const string CONFIGURATION_VARIANT_NAME = "configuration";
    private const string JSON_PAYLOAD_TYPE = "json";

    private static readonly Dictionary<string, string?> EMPTY_OVERRIDES = new (StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> EMPTY_SKIPS = new (StringComparer.OrdinalIgnoreCase);

    private ILogger logger = bootstrapLogger;
    private Dictionary<string, string?> appliedOverrides = EMPTY_OVERRIDES;
    private HashSet<string> skippedKeys = EMPTY_SKIPS;
    private bool initialLoadDone;

    /// <summary>
    ///     The overrides currently in effect, for diagnostics. Empty until a document is applied;
    ///     the snapshot is never mutated, a later document publishes a new instance.
    ///     <para />
    ///     Names whatever keys the remote document set, minus any the type or shape check skipped.
    /// </summary>
    public IReadOnlyDictionary<string, string?> AppliedOverrides => Volatile.Read(ref appliedOverrides);

    /// <summary>
    ///     Blocking first fetch, bounded by
    ///     <see cref="FeatureFlagsOptions.InitialFetchTimeoutSeconds" /> and failing open: any
    ///     failure logs, leaves this provider empty and lets startup continue on the shipped
    ///     defaults from the lower-precedence sources.
    ///     <para />
    ///     Runs at most once. <c>IConfigurationRoot.Reload()</c> calls <c>Load</c> on every provider,
    ///     and this one has no business issuing a blocking HTTP request from whichever thread raised
    ///     that; <see cref="FeatureFlagsPoller" /> owns refreshes from then on.
    /// </summary>
    public override void Load()
    {
        if (initialLoadDone)
            return;

        initialLoadDone = true;

        if (!options.Enabled)
        {
            logger.LogWarning("Feature flags disabled (FeatureFlags:Enabled is false); shipped defaults apply");
            return;
        }

        if (options.InitialFetchTimeoutSeconds <= 0)
        {
            logger.LogWarning(
                "Feature flags initial fetch skipped (FeatureFlags:InitialFetchTimeoutSeconds is zero); running on shipped defaults until the first poll lands");

            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.InitialFetchTimeoutSeconds));

            // Configuration providers load synchronously, before the host and its schedulers exist.
            Apply(client.FetchAsync(timeout.Token).GetAwaiter().GetResult());
        }
        catch (Exception e)
        {
            logger.LogError(e, "Initial feature flags fetch from {Url} failed; starting on shipped defaults", client.Url);
        }
    }

    /// <summary>
    ///     Projects <paramref name="document" /> onto configuration keys, swaps them in and fires
    ///     the reload token. Never throws: a malformed payload or a consumer that rejects the
    ///     document leaves the previous overrides in effect, and a value that fails the type or
    ///     shape check costs only its own key.
    ///     <para />
    ///     The keys this apply skipped are remembered so the next one can tell a newly broken key
    ///     from one that was already broken; a payload that failed to parse at all leaves the
    ///     previous set standing, having established nothing about any key.
    /// </summary>
    public void Apply(FeatureFlagsDocument document)
    {
        Dictionary<string, string?> next;
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try { next = BuildOverrides(document, skipped); }
        catch (Exception e)
        {
            logger.LogWarning(e, "Feature flags document carried a malformed configuration payload; retaining previous overrides");
            return;
        }

        Volatile.Write(ref skippedKeys, skipped);

        Swap(next);
    }

    /// <summary>
    ///     Replaces the bootstrap logger given at construction with the host's configured one.
    ///     Configuration sources are constructed before DI, and therefore before logging exists.
    /// </summary>
    public void UseLogger(ILogger replacement) => logger = replacement;

    private Dictionary<string, string?> BuildOverrides(FeatureFlagsDocument document, HashSet<string> skipped)
    {
        var next = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (document.Flags is null)
            return next;

        foreach ((string flag, bool enabled) in document.Flags)
        {
            if (!enabled)
                continue;

            string? payload = FindConfigurationPayload(document, flag);

            // A flag with no usable configuration variant is an ordinary feature flag, not a
            // configuration fragment.
            if (payload is not null)
                AddFragment(next, flag, payload, skipped);
        }

        return next;
    }

    /// <summary>
    ///     The JSON body of the flag's enabled <c>configuration</c> variant, or null when it carries
    ///     no usable one.
    /// </summary>
    private static string? FindConfigurationPayload(FeatureFlagsDocument document, string flag)
    {
        if (document.Variants is null || !document.Variants.TryGetValue(flag, out FeatureFlagVariant? variant))
            return null;

        if (!variant.Enabled || !string.Equals(variant.Name, CONFIGURATION_VARIANT_NAME, StringComparison.OrdinalIgnoreCase))
            return null;

        FeatureFlagVariantPayload? payload = variant.Payload;

        if (payload is null || !string.Equals(payload.Type, JSON_PAYLOAD_TYPE, StringComparison.OrdinalIgnoreCase))
            return null;

        return string.IsNullOrWhiteSpace(payload.Value) ? null : payload.Value;
    }

    /// <summary>
    ///     Flattens one <c>configuration</c> payload through Microsoft's own JSON configuration
    ///     parser — so nesting and value coercion behave as they do for <c>appsettings.json</c> —
    ///     then copies its well-typed leaves into <paramref name="next" />. Malformed JSON propagates
    ///     so the whole document can be discarded.
    /// </summary>
    private void AddFragment(Dictionary<string, string?> next, string flag, string payload, HashSet<string> skipped)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        IConfigurationRoot flattened = new ConfigurationBuilder().AddJsonStream(stream).Build();

        using (flattened as IDisposable)
        {
            foreach (KeyValuePair<string, string?> pair in flattened.AsEnumerable())
            {
                // Section nodes carry a null value; only leaves name an overridable knob.
                if (pair.Value is null)
                    continue;

                if (IsWellShaped(flag, pair.Key, skipped) && IsWellTyped(flag, pair.Key, pair.Value, skipped))
                    next[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    ///     Whether <paramref name="key" /> is a knob at all, rather than one leaf of a JSON array or
    ///     object written where the schema declares a scalar. Configuration flattens
    ///     <c>"Whitelist": ["a", "b"]</c> into <c>Whitelist:0</c> and <c>Whitelist:1</c>, and
    ///     <c>"Whitelist": [{"ip": "…"}]</c> into <c>Whitelist:0:ip</c> — none of which any option
    ///     binds to, while <c>Whitelist</c> itself keeps its shipped default, so the document reads as
    ///     applied while the knob it meant to set never moves. Any depth under the declared scalar
    ///     counts. Skipped per key, like an unbindable value, and logged with the scalar it
    ///     contradicts.
    /// </summary>
    private bool IsWellShaped(string flag, string key, HashSet<string> skipped)
    {
        if (!schema.IsUnderScalarKey(key, out string scalarKey))
            return true;

        if (IsNewlySkipped(key, skipped))
            logger.LogWarning(
                "Feature flag {Flag} set {Key}, but {ScalarKey} is a scalar knob per {SchemaFile} — a JSON array flattens into indexed keys and leaves the knob itself on its default; list-shaped knobs are comma-separated strings; skipping that key and applying the rest of the document",
                flag, key, scalarKey, DynamicConfigSchema.FILE_NAME);

        return false;
    }

    /// <summary>
    ///     Whether <paramref name="value" /> could bind to the type its shipped default in
    ///     <c>dynamicconfig.json</c> declares. One that could not — <c>"ten"</c> for a numeric knob —
    ///     would throw inside the options binder on whichever thread first read it, so that key is
    ///     left out and keeps the value the lower-precedence sources give it. Only that key: the
    ///     rest of the document is other knobs, and a typo in one is no reason to withhold them.
    ///     Logs the offending key, its value and the type expected.
    /// </summary>
    private bool IsWellTyped(string flag, string key, string value, HashSet<string> skipped)
    {
        if (schema.Accepts(key, value, out string expectedType))
            return true;

        if (IsNewlySkipped(key, skipped))
            logger.LogWarning(
                "Feature flag {Flag} set {Key} to '{Value}', which is not a valid {ExpectedType} per {SchemaFile}; skipping that key and applying the rest of the document",
                flag, key, value, expectedType, DynamicConfigSchema.FILE_NAME);

        return false;
    }

    /// <summary>
    ///     Records <paramref name="key" /> in <paramref name="skipped" /> — this apply's set, which
    ///     becomes the remembered one — and reports whether the previous apply had not already
    ///     skipped it, which is the condition for warning about it.
    ///     <para />
    ///     The poller refetches on a fixed interval, so a document that stays broken would otherwise
    ///     repeat its warnings forever, one line per bad key per poll and one per element for an
    ///     array. Keying on the previous apply's set instead of on elapsed time means a key that
    ///     breaks, is fixed and breaks again warns each time it turns bad, and never in between.
    /// </summary>
    private bool IsNewlySkipped(string key, HashSet<string> skipped)
    {
        skipped.Add(key);

        return !Volatile.Read(ref skippedKeys).Contains(key);
    }

    /// <summary>
    ///     Publishes <paramref name="next" /> and fires the reload token, undoing both if a consumer
    ///     throws while rebinding. Transactional rather than optimistic because type validation
    ///     cannot foresee every way a binder refuses a value, and a poison value left live would
    ///     surface on a transport thread with nothing between it and the host's crash handler.
    ///     <para />
    ///     Logs the resulting key set only when it differs from the one already applied, so a poller
    ///     running on a seconds schedule stays silent until an override actually changes.
    /// </summary>
    private void Swap(Dictionary<string, string?> next)
    {
        IDictionary<string, string?> previousData = Data;
        Dictionary<string, string?> previousOverrides = Volatile.Read(ref appliedOverrides);

        Data = next;
        Volatile.Write(ref appliedOverrides, next);

        try { OnReload(); }
        catch (Exception e)
        {
            Data = previousData;
            Volatile.Write(ref appliedOverrides, previousOverrides);

            // Consumers that already rebound to the rejected document need a second notification to
            // come back off it, and that reload must not escape either.
            try { OnReload(); }
            catch (Exception rollbackFailure)
            {
                logger.LogError(
                    rollbackFailure, "Feature flags rollback notification failed after a rejected document");
            }

            logger.LogWarning(
                e, "Feature flags document was refused while consumers rebound to it; rolled back to the previous overrides");

            return;
        }

        if (!OverridesChanged(previousOverrides, next))
            return;

        logger.LogInformation(
            "Feature flag overrides changed; {KeyCount} key(s) now applied: {Keys}",
            next.Count, next.Count == 0 ? "(none)" : string.Join(", ", next.Keys));
    }

    /// <summary>
    ///     Whether the two override sets name different keys or give a shared key a different value.
    ///     Both are built with the same case-insensitive comparer, so equal counts plus a match for
    ///     every key of <paramref name="next" /> means the sets are identical.
    /// </summary>
    private static bool OverridesChanged(
        Dictionary<string, string?> previous, Dictionary<string, string?> next)
    {
        if (previous.Count != next.Count)
            return true;

        foreach ((string key, string? value) in next)
            if (!previous.TryGetValue(key, out string? existing) || existing != value)
                return true;

        return false;
    }
}
