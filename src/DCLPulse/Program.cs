using DCL.Auth;
using DCL.WebTransport;
using Decentraland.Pulse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pulse;
using Pulse.FeatureFlags;
using Pulse.InterestManagement;
using Pulse.Clusters;
using Pulse.Messaging;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Metrics.Console;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Geo;
using Pulse.Transport.Hardening;
using XenoAtom.Terminal.UI.Controls;
using ZLogger;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

// ── Runtime configuration ───────────────────────────────────────────────────────────────────────
// dynamicconfig.json carries the offline defaults for every remotely settable knob, and those
// defaults double as the type schema the remote document's values are checked against.
// AddDynamicConfig inserts it below the environment-variable provider rather than appending, so the
// defaults stay defaults: Transport__Hardening__IpLimiter__Enabled=true on a task definition wins.
// See DynamicConfigExtensions.
string dynamicConfigPath =
    Path.Combine(builder.Environment.ContentRootPath, DynamicConfigSchema.FILE_NAME);

builder.Configuration.AddDynamicConfig(dynamicConfigPath);

var featureFlagsOptions = builder.Configuration
                                 .GetSection(FeatureFlagsOptions.SECTION_NAME)
                                 .Get<FeatureFlagsOptions>() ?? new FeatureFlagsOptions();

var envName = new EnvName();
var featureFlagsClient = new FeatureFlagsClient(featureFlagsOptions, envName);

// Configuration sources are built before DI exists, so the blocking first load logs through a
// bootstrap logger; the host's own logger replaces it once builder.Build() has run.
ILoggerFactory bootstrapLoggerFactory =
    LoggerFactory.Create(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));

var featureFlagsSource = new PulseFlagsConfigurationSource(
    featureFlagsOptions,
    DynamicConfigSchema.LoadFromFile(dynamicConfigPath),
    featureFlagsClient,
    bootstrapLoggerFactory.CreateLogger("Pulse.FeatureFlags"));

// Appended last, so the chain is appsettings.json → appsettings.{Environment}.json →
// dynamicconfig.json → environment variables → command line → remote pulse.json. Remote overrides
// are the one layer meant to outrank an operator's environment — that is how a live server is
// reconfigured without a redeploy.
builder.Configuration.Sources.Add(featureFlagsSource);

builder.Services.AddSingleton(featureFlagsOptions);
builder.Services.AddSingleton(featureFlagsClient);
builder.Services.AddSingleton(featureFlagsSource.Provider);
builder.Services.AddHostedService<FeatureFlagsPoller>();

builder.Services.Configure<ENetTransportOptions>(
    builder.Configuration.GetSection(ENetTransportOptions.SECTION_NAME));

builder.Services.Configure<PreAuthAdmissionOptions>(
    builder.Configuration.GetSection(PreAuthAdmissionOptions.SECTION_NAME));

builder.Services.Configure<IpLimiterOptions>(
    builder.Configuration.GetSection(IpLimiterOptions.SECTION_NAME));

builder.Services.Configure<CorruptedPacketLimiterOptions>(
    builder.Configuration.GetSection(CorruptedPacketLimiterOptions.SECTION_NAME));

builder.Services.Configure<HandshakeAttemptPolicyOptions>(
    builder.Configuration.GetSection(HandshakeAttemptPolicyOptions.SECTION_NAME));

builder.Services.Configure<MovementInputRateLimiterOptions>(
    builder.Configuration.GetSection(MovementInputRateLimiterOptions.SECTION_NAME));

builder.Services.Configure<DiscreteEventRateLimiterOptions>(
    builder.Configuration.GetSection(DiscreteEventRateLimiterOptions.SECTION_NAME));

builder.Services.Configure<FieldValidatorOptions>(
    builder.Configuration.GetSection(FieldValidatorOptions.SECTION_NAME));

builder.Services.Configure<SceneListenerOptions>(
    builder.Configuration.GetSection(SceneListenerOptions.SECTION_NAME));

builder.Services.Configure<HandshakeReplayPolicyOptions>(
    builder.Configuration.GetSection(HandshakeReplayPolicyOptions.SECTION_NAME));

builder.Services.Configure<BansOptions>(
    builder.Configuration.GetSection(BansOptions.SECTION_NAME));

builder.Services.AddSingleton<PreAuthAdmission>();
builder.Services.AddSingleton<IpLimiter>();
builder.Services.AddSingleton<CorruptedPacketLimiter>();
builder.Services.AddSingleton<HandshakeAttemptPolicy>();
builder.Services.AddSingleton<MovementInputRateLimiter>();
builder.Services.AddSingleton<DiscreteEventRateLimiter>();
builder.Services.AddSingleton<FieldValidator>();
builder.Services.AddSingleton<HandshakeReplayPolicy>();
builder.Services.AddSingleton<BanList>();
builder.Services.AddSingleton<BanEnforcer>();

builder.Services.Configure<PeerOptions>(
    builder.Configuration.GetSection(PeerOptions.SECTION_NAME));

// Resolve PeerOptions directly for services that don't use IOptions<T>
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PeerOptions>>().Value);

builder.Services.AddSingleton<ITimeProvider, StopwatchTimeProvider>();

builder.Services.AddSingleton(sp =>
{
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;

    string directory = Path.IsPathRooted(transportOptions.GeoDbDirectory)
        ? transportOptions.GeoDbDirectory
        : Path.Combine(AppContext.BaseDirectory, transportOptions.GeoDbDirectory);

    return ContinentResolver.LoadFromDirectory(directory, sp.GetRequiredService<ILogger<ContinentResolver>>());
});

builder.Services.AddSingleton<ENetHostedService>();
builder.Services.AddHostedService<ENetHostedService>(sp => sp.GetRequiredService<ENetHostedService>());
builder.Services.AddSingleton<ITransport, MessagePipeTransport>();

// WebTransport (browser transport) — opt-in, off by default. When disabled the server behaves exactly
// as ENet-only. When enabled it shares the PeerIndex pool, hardening, MessagePipe, and workers with
// ENet; the certificate comes from config or a generated self-signed dev cert.
builder.Services.Configure<WebTransportOptions>(
    builder.Configuration.GetSection(WebTransportOptions.SECTION_NAME));

bool webTransportEnabled = builder.Configuration.GetSection(WebTransportOptions.SECTION_NAME)
                                  .GetValue<bool>(nameof(WebTransportOptions.Enabled));

if (webTransportEnabled)
{
    builder.Services.AddSingleton<IWebTransportHost>(sp =>
    {
        WebTransportOptions webTransportOptions = sp.GetRequiredService<IOptions<WebTransportOptions>>().Value;
        bool allowSelfSigned = sp.GetRequiredService<IHostEnvironment>().IsDevelopment();
        ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pulse.Transport.WebTransport");
        (string certPem, string keyPem) = WebTransportCertificate.Resolve(webTransportOptions, allowSelfSigned, logger);
        return new WebTransportHostAdapter(WebTransportHost.Create(webTransportOptions.BindAddr, certPem, keyPem));
    });

    builder.Services.AddHostedService<WebTransportHostedService>();
}

builder.Services.AddHostedService<PeersManager>();
builder.Services.AddSingleton<MessagePipe>();
builder.Services.AddSingleton(new ClientMessageCounters());
builder.Services.AddSingleton(new ServerMessageCounters());
builder.Services.AddSingleton<PeerStateFactory>();
builder.Services.AddSingleton<PlayerStateInputHandler>();
builder.Services.AddSingleton<ResyncRequestHandler>();
builder.Services.AddSingleton<HandshakeHandler>();
builder.Services.AddSingleton<ProfileAnnouncementHandler>();
builder.Services.AddSingleton<EmoteStartHandler>();
builder.Services.AddSingleton<EmoteStopHandler>();
builder.Services.AddSingleton<EmoteCompleter>();
builder.Services.AddSingleton<TeleportHandler>();
builder.Services.AddSingleton(new AuthChainValidator(new RustEthereumSignVerifier()));
builder.Services.AddSingleton<SceneListenerCellMapper>();
builder.Services.AddSingleton<SceneListenerHandshakeHandler>();
builder.Services.AddSingleton<SceneListenerUpdateHandler>();

builder.Services.AddSingleton(sp => new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>
{
    { ClientMessage.MessageOneofCase.Handshake, sp.GetRequiredService<HandshakeHandler>() },
    { ClientMessage.MessageOneofCase.Input, sp.GetRequiredService<PlayerStateInputHandler>() },
    { ClientMessage.MessageOneofCase.Resync, sp.GetRequiredService<ResyncRequestHandler>() },
    { ClientMessage.MessageOneofCase.ProfileAnnouncement, sp.GetRequiredService<ProfileAnnouncementHandler>() },
    { ClientMessage.MessageOneofCase.EmoteStart, sp.GetRequiredService<EmoteStartHandler>() },
    { ClientMessage.MessageOneofCase.EmoteStop, sp.GetRequiredService<EmoteStopHandler>() },
    {ClientMessage.MessageOneofCase.Teleport, sp.GetRequiredService<TeleportHandler>() },
    { ClientMessage.MessageOneofCase.SceneListenerHandshake, sp.GetRequiredService<SceneListenerHandshakeHandler>() },
    { ClientMessage.MessageOneofCase.SceneListenerUpdate, sp.GetRequiredService<SceneListenerUpdateHandler>() },
});

builder.Services.AddSingleton<ProfileBoard>(sp =>
{
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;
    return new ProfileBoard(transportOptions.MaxPeers);
});

// Simulation
builder.Services.AddSingleton(sp =>
{
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;
    PeerOptions peerOptions = sp.GetRequiredService<PeerOptions>();
    return new SnapshotBoard(transportOptions.MaxPeers, peerOptions.SnapshotHistoryCapacity);
});

builder.Services.AddSingleton(sp =>
{
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;
    return new IdentityBoard(transportOptions.MaxPeers);
});

builder.Services.AddSingleton<IPeerIndexAllocator>(sp =>
{
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;
    return new PeerIndexAllocator(transportOptions.MaxPeers);
});

builder.Services.AddSingleton<IValidateOptions<SpatialHashAreaOfInterestOptions>, SpatialHashAreaOfInterestOptionsValidator>();

builder.Services.AddOptions<SpatialHashAreaOfInterestOptions>()
    .Bind(builder.Configuration.GetSection(SpatialHashAreaOfInterestOptions.SECTION_NAME))
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SpatialHashAreaOfInterestOptions>>().Value);

builder.Services.AddSingleton(sp =>
{
    SpatialHashAreaOfInterestOptions aoiOptions = sp.GetRequiredService<SpatialHashAreaOfInterestOptions>();
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;
    return new RealmSpatialGrids(aoiOptions.CellSize, transportOptions.MaxPeers);
});

builder.Services.AddSingleton<IAreaOfInterest, SpatialHashAreaOfInterest>();

builder.Services.AddSingleton<PeerSnapshotPublisher>();

// Clusters — derived off the hot path by a background pass over the AoI boards. The publisher is
// registered even when NATS is unconfigured, where it serves as a no-op IClusterFeedPublisher.
builder.Services.AddOptions<ClusterOptions>()
    .Bind(builder.Configuration.GetSection(ClusterOptions.SECTION_NAME));

// Accepts the flat NATS_URL alongside Nats__Url. Must run before the bind below.
builder.Configuration.AddNatsUrlAlias(Environment.GetEnvironmentVariable(NatsOptions.URL_ENV_ALIAS));

builder.Services.AddOptions<NatsOptions>()
    .Bind(builder.Configuration.GetSection(NatsOptions.SECTION_NAME));

builder.Services.AddSingleton<ClusterBoard>();

builder.Services.AddSingleton<NatsPublisher>();
builder.Services.AddSingleton<IClusterFeedPublisher>(sp => sp.GetRequiredService<NatsPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<NatsPublisher>());

builder.Services.AddSingleton(sp =>
{
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;

    return new ClusterTracker(
        sp.GetRequiredService<ILogger<ClusterTracker>>(),
        sp.GetRequiredService<IOptions<ClusterOptions>>(),
        sp.GetRequiredService<RealmSpatialGrids>(),
        sp.GetRequiredService<SnapshotBoard>(),
        sp.GetRequiredService<IdentityBoard>(),
        sp.GetRequiredService<ClusterBoard>(),
        sp.GetRequiredService<IClusterFeedPublisher>(),
        transportOptions.MaxPeers);
});

builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterTracker>());

var dashboardType = builder.Configuration.GetSection(MetricsOptions.SECTION_NAME)
                          .GetValue<DashboardType>(nameof(MetricsOptions.Type));

builder.Services.Configure<MetricsOptions>(
    builder.Configuration.GetSection(MetricsOptions.SECTION_NAME));

builder.Services.AddSingleton<IMetricsCollector, MeterListenerMetricsCollector>();
builder.Services.AddHostedService(sp => (MeterListenerMetricsCollector)sp.GetRequiredService<IMetricsCollector>());

if (dashboardType == DashboardType.Console)
{
    var logControl = new LogControl();
    var dashboardLoggerProvider = new DashboardLoggerProvider();
    builder.Services.AddSingleton(logControl);
    builder.Services.AddSingleton(dashboardLoggerProvider);
    builder.Services.AddHostedService<ConsoleDashboard>();
    builder.Logging.AddProvider(dashboardLoggerProvider);
}
else
{
    builder.Logging.AddZLoggerConsole(options =>
    {
        options.UsePlainTextFormatter(formatter =>
        {
            formatter.SetPrefixFormatter($"{0}{1}{2}: {3}\n      ",
                (in template, in info) =>
                {
                    (string open, string close) = LogLevelStyle.GetAnsiEscape(info.LogLevel);
                    template.Format(open, LogLevelStyle.GetPrefix(info.LogLevel), close, info.Category);
                });
        });
    });
}

builder.Services.Configure<HttpServiceOptions>(
    builder.Configuration.GetSection(HttpServiceOptions.SECTION_NAME));

builder.Services.AddSingleton<MetricsBearerToken>();
builder.Services.AddSingleton<CommsBearerToken>();
builder.Services.AddSingleton(envName);
builder.Services.AddHostedService<HttpService>();
builder.Services.AddHostedService<BansPollingHttpService>();

builder.Services.Configure<ParcelEncoderOptions>(
    builder.Configuration.GetSection(ParcelEncoderOptions.SECTION_NAME));

builder.Services.AddSingleton<ParcelEncoder>();

IHost host = builder.Build();

// Swap the bootstrap console logger for the host's, so poll-time warnings don't corrupt the
// ConsoleDashboard TUI.
featureFlagsSource.Provider.UseLogger(
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pulse.FeatureFlags"));

bootstrapLoggerFactory.Dispose();

if (!webTransportEnabled)
    host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Pulse.Transport.WebTransport")
        .LogWarning(
            "WebTransport is disabled (WebTransport:Enabled=false); only ENet is serving. Set WebTransport:Enabled=true to accept browser clients.");

host.Run();
