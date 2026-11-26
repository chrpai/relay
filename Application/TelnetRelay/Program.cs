using TelnetRelay;

var builder = Host.CreateApplicationBuilder(args);

// Runs as a Windows service when started by the Service Control Manager,
// and as a normal console app otherwise (handy for testing). As a service,
// appsettings.json is read from the folder the .exe is in.
builder.Services.AddWindowsService(options => options.ServiceName = "TelnetRelay");

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
