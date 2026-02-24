using Pulse.Transport;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ENetTransportOptions>(
    builder.Configuration.GetSection(ENetTransportOptions.SECTION_NAME));

builder.Services.AddHostedService<ENetHostedService>();

IHost host = builder.Build();
host.Run();
