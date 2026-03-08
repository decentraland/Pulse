using Pulse.Messaging;
using Pulse.Peers;
using Pulse.Transport;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ENetTransportOptions>(
    builder.Configuration.GetSection(ENetTransportOptions.SECTION_NAME));

builder.Services.Configure<PeerOptions>(
    builder.Configuration.GetSection(PeerOptions.SECTION_NAME));

builder.Services.AddHostedService<ENetHostedService>();
builder.Services.AddHostedService<PeersManager>();

builder.Services.AddSingleton<MessagePipe>();
builder.Services.AddSingleton<PeerStateFactory>();

builder.Services.AddSingleton<PlayerStateInputHandler>();

IHost host = builder.Build();
host.Run();
