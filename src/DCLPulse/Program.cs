using DCL.Auth;
using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Metrics;
using Pulse.Metrics.Console;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using XenoAtom.Terminal.UI.Controls;
using ZLogger;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

builder.Services.Configure<ENetTransportOptions>(
    builder.Configuration.GetSection(ENetTransportOptions.SECTION_NAME));

builder.Services.Configure<PeerOptions>(
    builder.Configuration.GetSection(PeerOptions.SECTION_NAME));

// Resolve PeerOptions directly for services that don't use IOptions<T>
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PeerOptions>>().Value);

builder.Services.AddSingleton<ITimeProvider, StopwatchTimeProvider>();

builder.Services.AddSingleton<ENetHostedService>();
builder.Services.AddHostedService<ENetHostedService>(sp => sp.GetRequiredService<ENetHostedService>());
builder.Services.AddSingleton<ITransport>(sp => sp.GetRequiredService<ENetHostedService>());
builder.Services.AddHostedService<PeersManager>();
builder.Services.AddSingleton<MessagePipe>();
builder.Services.AddSingleton(new ClientMessageCounters(8));
builder.Services.AddSingleton(new ServerMessageCounters(10));
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

builder.Services.AddSingleton(sp => new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>
{
    { ClientMessage.MessageOneofCase.Handshake, sp.GetRequiredService<HandshakeHandler>() },
    { ClientMessage.MessageOneofCase.Input, sp.GetRequiredService<PlayerStateInputHandler>() },
    { ClientMessage.MessageOneofCase.Resync, sp.GetRequiredService<ResyncRequestHandler>() },
    { ClientMessage.MessageOneofCase.ProfileAnnouncement, sp.GetRequiredService<ProfileAnnouncementHandler>() },
    { ClientMessage.MessageOneofCase.EmoteStart, sp.GetRequiredService<EmoteStartHandler>() },
    { ClientMessage.MessageOneofCase.EmoteStop, sp.GetRequiredService<EmoteStopHandler>() },
    {ClientMessage.MessageOneofCase.Teleport, sp.GetRequiredService<TeleportHandler>() },
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

builder.Services.Configure<SpatialHashAreaOfInterestOptions>(
    builder.Configuration.GetSection(SpatialHashAreaOfInterestOptions.SECTION_NAME));

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SpatialHashAreaOfInterestOptions>>().Value);

builder.Services.AddSingleton(sp =>
{
    SpatialHashAreaOfInterestOptions aoiOptions = sp.GetRequiredService<SpatialHashAreaOfInterestOptions>();
    ENetTransportOptions transportOptions = sp.GetRequiredService<IOptions<ENetTransportOptions>>().Value;
    return new SpatialGrid(aoiOptions.CellSize, transportOptions.MaxPeers);
});

builder.Services.AddSingleton<IAreaOfInterest, SpatialHashAreaOfInterest>();

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
builder.Services.AddSingleton(new MetricsBearerToken(Environment.GetEnvironmentVariable(MetricsBearerToken.ENV_VAR)));
builder.Services.AddHostedService<HttpService>();

builder.Services.Configure<ParcelEncoderOptions>(
    builder.Configuration.GetSection(ParcelEncoderOptions.SECTION_NAME));

builder.Services.AddSingleton<ParcelEncoder>();

IHost host = builder.Build();
host.Run();
