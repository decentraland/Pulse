using DCL.Auth;
using Decentraland.Pulse;
using Microsoft.Extensions.Options;
using Pulse;
using Pulse.InterestManagement;
using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddSingleton<PeerStateFactory>();
builder.Services.AddSingleton<PlayerStateInputHandler>();
builder.Services.AddSingleton<ResyncRequestHandler>();
builder.Services.AddSingleton<HandshakeHandler>();
builder.Services.AddSingleton(new AuthChainValidator(new NethereumPersonalSignVerifier()));

builder.Services.AddSingleton(sp => new Dictionary<ClientMessage.MessageOneofCase, IMessageHandler>
{
    { ClientMessage.MessageOneofCase.Handshake, sp.GetRequiredService<HandshakeHandler>() },
    { ClientMessage.MessageOneofCase.Input, sp.GetRequiredService<PlayerStateInputHandler>() },
    { ClientMessage.MessageOneofCase.Resync, sp.GetRequiredService<ResyncRequestHandler>() },
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

builder.Services.Configure<SpatialHashAreaOfInterestOptions>(
    builder.Configuration.GetSection(SpatialHashAreaOfInterestOptions.SECTION_NAME));

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SpatialHashAreaOfInterestOptions>>().Value);

builder.Services.AddSingleton(sp =>
{
    SpatialHashAreaOfInterestOptions aoiOptions = sp.GetRequiredService<SpatialHashAreaOfInterestOptions>();
    return new SpatialGrid(aoiOptions.CellSize);
});

builder.Services.AddSingleton<IAreaOfInterest, SpatialHashAreaOfInterest>();

IHost host = builder.Build();
host.Run();
