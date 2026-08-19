using Microsoft.Extensions.Logging;
using NSubstitute;
using Pulse;
using Pulse.FeatureFlags;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DCLPulseTests.FeatureFlags;

/// <summary>
///     Shared setup for the feature-flag fixtures: a provider over the shipped type schema, and
///     documents in the shape Unleash actually serves.
/// </summary>
internal static class FeatureFlagsTestDoubles
{
    /// <summary>
    ///     Verbatim body served by the live <c>pulse.json</c> when the per-IP limiter shipped, kept as
    ///     the one case that is not hand-shaped by a test: flag and variant keys still carry the
    ///     <c>pulse-</c> application prefix, and the configuration fragment arrives as an escaped JSON
    ///     string inside the variant payload.
    /// </summary>
    public const string REAL_DOCUMENT_BODY =
        """{"flags":{"pulse-hardening":true},"variants":{"pulse-hardening":{"name":"configuration","payload":{"type":"json","value":"{\n  \"Transport\": {\n    \"Hardening\": {\n      \"IpLimiter\": {\n        \"Enabled\": true,\n        \"MaxConcurrency\": 10,\n        \"Whitelist\": \"\"\n      }\n    }\n  }\n}"},"enabled":true}}}""";

    /// <summary>The three leaves the real document sets, in <c>dynamicconfig.json</c> key form.</summary>
    public const string ENABLED_KEY = "Transport:Hardening:IpLimiter:Enabled";
    public const string MAX_CONCURRENCY_KEY = "Transport:Hardening:IpLimiter:MaxConcurrency";
    public const string WHITELIST_KEY = "Transport:Hardening:IpLimiter:Whitelist";

    /// <summary>The section the three leaves above live under, for binding a stub options type.</summary>
    public const string IP_LIMITER_SECTION = "Transport:Hardening:IpLimiter";

    /// <summary>A well-formed fragment: every value matches the type its shipped default declares.</summary>
    public const string FRAGMENT =
        """{"Transport":{"Hardening":{"IpLimiter":{"Enabled":true,"MaxConcurrency":25,"Whitelist":"10.0.0.1"}}}}""";

    /// <summary>
    ///     The typo that motivated the type gate, next to two siblings that are fine.
    ///     <c>MaxConcurrency</c> has an integer default in <c>dynamicconfig.json</c>, so its type is
    ///     known and <c>"ten"</c> is provably unbindable.
    /// </summary>
    public const string POISON_FRAGMENT =
        """{"Transport":{"Hardening":{"IpLimiter":{"Enabled":true,"MaxConcurrency":"ten","Whitelist":""}}}}""";

    /// <summary>
    ///     A JSON array written where <c>dynamicconfig.json</c> declares a string. The configuration
    ///     flattener turns it into <c>Whitelist:0</c> and <c>Whitelist:1</c>, leaving <c>Whitelist</c>
    ///     itself unset — the shape the shape gate exists to catch.
    /// </summary>
    public const string ARRAY_WHITELIST_FRAGMENT =
        """{"Transport":{"Hardening":{"IpLimiter":{"Enabled":true,"Whitelist":["10.0.0.1","10.0.0.2"]}}}}""";

    /// <summary>The indexed keys <see cref="ARRAY_WHITELIST_FRAGMENT" /> flattens into.</summary>
    public const string WHITELIST_INDEX_0_KEY = "Transport:Hardening:IpLimiter:Whitelist:0";
    public const string WHITELIST_INDEX_1_KEY = "Transport:Hardening:IpLimiter:Whitelist:1";

    /// <summary>
    ///     The same mistake nested one level deeper: an array of objects rather than of strings.
    ///     Configuration flattens it to <c>Whitelist:0:ip</c>, whose immediate parent
    ///     <c>Whitelist:0</c> is undeclared — only <c>Whitelist</c> two levels up is the scalar it
    ///     contradicts, so a shape check that inspects one ancestor lets it through.
    /// </summary>
    public const string NESTED_ARRAY_WHITELIST_FRAGMENT =
        """{"Transport":{"Hardening":{"IpLimiter":{"Enabled":true,"Whitelist":[{"ip":"10.0.0.1"}]}}}}""";

    /// <summary>The key <see cref="NESTED_ARRAY_WHITELIST_FRAGMENT" /> flattens into.</summary>
    public const string WHITELIST_NESTED_IP_KEY = "Transport:Hardening:IpLimiter:Whitelist:0:ip";

    /// <summary>
    ///     <see cref="ARRAY_WHITELIST_FRAGMENT" /> with a second, differently broken key added, for
    ///     telling "already skipped last apply" from "newly broken".
    /// </summary>
    public const string ARRAY_WHITELIST_AND_POISON_FRAGMENT =
        """{"Transport":{"Hardening":{"IpLimiter":{"Enabled":true,"MaxConcurrency":"ten","Whitelist":["10.0.0.1","10.0.0.2"]}}}}""";

    /// <summary>The same typo with nothing else in the document, so skipping it leaves nothing.</summary>
    public const string ONLY_POISON_FRAGMENT =
        """{"Transport":{"Hardening":{"IpLimiter":{"MaxConcurrency":"ten"}}}}""";

    /// <summary>
    ///     A key <c>dynamicconfig.json</c> declares no default for. Nothing says what type it should
    ///     be, so nothing can check it — it is applied as written.
    /// </summary>
    public const string UNDECLARED_KEY_FRAGMENT =
        """{"Peers":{"ResyncWithDelta":true}}""";

    /// <summary>The leaf <see cref="UNDECLARED_KEY_FRAGMENT" /> sets.</summary>
    public const string UNDECLARED_KEY = "Peers:ResyncWithDelta";

    /// <summary>
    ///     A quoted number — a hand-authoring slip that the configuration binder converts without
    ///     complaint, so the gate must not reject it.
    /// </summary>
    public const string QUOTED_NUMBER_FRAGMENT =
        """{"Transport":{"Hardening":{"IpLimiter":{"Enabled":true,"MaxConcurrency":"20","Whitelist":""}}}}""";

    // Apply() never touches the client — only the blocking Load() fetches — so every Apply-driven test
    // shares one instance pointed at a port nothing listens on.
    private static readonly FeatureFlagsClient IDLE_CLIENT =
        new (new FeatureFlagsOptions { Url = "http://127.0.0.1:1" }, new EnvName());

    /// <summary>
    ///     A provider over the real checked-in <c>dynamicconfig.json</c> copied next to the test
    ///     assembly — the one that carries real types, so type checking is exercised against the
    ///     shipped schema rather than a stub.
    /// </summary>
    public static PulseFlagsConfigurationProvider Provider(
        ILogger? logger = null,
        FeatureFlagsClient? client = null) =>
        new (new FeatureFlagsOptions(),
            ShippedSchema(),
            client ?? IDLE_CLIENT,
            logger ?? Substitute.For<ILogger>());

    /// <summary>
    ///     A source over the shipped schema, for fixtures that need the provider inside a real
    ///     <c>IConfigurationRoot</c>. The initial fetch is switched off so building the root stays
    ///     offline and deterministic.
    /// </summary>
    public static PulseFlagsConfigurationSource Source() =>
        new (new FeatureFlagsOptions { InitialFetchTimeoutSeconds = 0 },
            ShippedSchema(),
            IDLE_CLIENT,
            Substitute.For<ILogger>());

    /// <summary>The checked-in <c>dynamicconfig.json</c>, copied next to the test assembly.</summary>
    public static DynamicConfigSchema ShippedSchema() =>
        DynamicConfigSchema.LoadFromFile(
            Path.Combine(AppContext.BaseDirectory, DynamicConfigSchema.FILE_NAME));

    /// <summary>The live document, parsed as <see cref="FeatureFlagsClient" /> would parse it.</summary>
    public static FeatureFlagsDocument RealDocument() => Parse(REAL_DOCUMENT_BODY);

    /// <summary>
    ///     A document whose single flag carries a <c>configuration</c> variant holding
    ///     <paramref name="fragmentJson" />. <paramref name="enabled" /> flips only the flag, leaving
    ///     the variant in place — the kill-switch shape an operator produces in Unleash.
    /// </summary>
    public static FeatureFlagsDocument WithFragment(string flag, string fragmentJson, bool enabled = true) =>
        new ()
        {
            Flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { [flag] = enabled },
            Variants = new Dictionary<string, FeatureFlagVariant>(StringComparer.OrdinalIgnoreCase)
            {
                [flag] = new ()
                {
                    Name = "configuration",
                    Enabled = true,
                    Payload = new FeatureFlagVariantPayload { Type = "json", Value = fragmentJson },
                },
            },
        };

    /// <summary>
    ///     The wire body Unleash would serve for <paramref name="fragmentJson" />, prefixed flag key
    ///     and escaped payload string included, for the fixtures that go through a real fetch.
    /// </summary>
    public static string DocumentBody(string fragmentJson) =>
        JsonSerializer.Serialize(new
        {
            flags = new Dictionary<string, bool> { ["pulse-hardening"] = true },
            variants = new Dictionary<string, object>
            {
                ["pulse-hardening"] = new
                {
                    name = "configuration",
                    enabled = true,
                    payload = new { type = "json", value = fragmentJson },
                },
            },
        });

    private static FeatureFlagsDocument Parse(string body) =>
        JsonSerializer.Deserialize<FeatureFlagsDocument>(body)
        ?? throw new InvalidOperationException("Test document deserialized to null");
}

/// <summary>
///     Minimal loopback stand-in for the flags CDN. Serves one fixed body on any path, so the client's
///     own URL construction decides what is requested.
/// </summary>
internal sealed class StubFlagsEndpoint : IDisposable
{
    private readonly HttpListener listener = new ();
    private readonly CancellationTokenSource shutdown = new ();
    private readonly Task serving;
    private readonly byte[] body;
    private readonly int statusCode;

    public StubFlagsEndpoint(string body, int statusCode = 200)
    {
        this.body = Encoding.UTF8.GetBytes(body);
        this.statusCode = statusCode;

        Origin = $"http://localhost:{FreePort()}";
        listener.Prefixes.Add($"{Origin}/");
        listener.Start();
        serving = ServeAsync();
    }

    public string Origin { get; }

    public void Dispose()
    {
        shutdown.Cancel();
        listener.Stop();
        serving.Wait(TimeSpan.FromSeconds(5));
        listener.Close();
        shutdown.Dispose();
    }

    /// <summary>A client whose document URL resolves to this endpoint.</summary>
    public FeatureFlagsClient Client() =>
        new (new FeatureFlagsOptions { Url = Origin, AppName = "pulse" }, new EnvName());

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                HttpListenerContext ctx = await listener.GetContextAsync().WaitAsync(shutdown.Token);

                ctx.Response.StatusCode = statusCode;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body, shutdown.Token);
                ctx.Response.Close();
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }
}
