using Factory.Infrastructure;
using Factory.Orchestrator;
using OpenTelemetry.Trace;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(configuration => configuration.WriteTo.Console().WriteTo.File("logs/orchestrator-.log", rollingInterval: RollingInterval.Day));
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource("Factory.Orchestrator"));
builder.Services.AddFactoryInfrastructure(builder.Configuration);
builder.Services.AddSingleton<TaskExecutor>();
builder.Services.AddSingleton<LeaseMonitor>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
