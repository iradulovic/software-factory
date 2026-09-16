using Factory.GitHubSync;
using Factory.Infrastructure;
using OpenTelemetry.Trace;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(configuration => configuration.WriteTo.Console().WriteTo.File("logs/github-sync-.log", rollingInterval: RollingInterval.Day));
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource("Factory.GitHubSync"));
builder.Services.AddFactoryInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
