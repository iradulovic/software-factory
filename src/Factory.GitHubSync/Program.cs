using Factory.GitHubSync;
using Factory.Infrastructure;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
// A real repository (owner/name/clone URL) is developer- or machine-specific, unlike the disabled placeholder
// checked into appsettings.json - appsettings.Local.json (gitignored) overrides it without ever needing to edit
// the tracked template.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.AddSerilog(configuration => configuration.WriteTo.Console().WriteTo.File("logs/github-sync-.log", rollingInterval: RollingInterval.Day));
builder.Services.AddFactoryTelemetry(builder.Configuration, "Factory.GitHubSync");
builder.Services.AddFactoryInfrastructure(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<DigestWorker>();
await builder.Build().RunAsync();
