using Factory.GitHubSync;
using Factory.Infrastructure;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog(configuration => configuration.WriteTo.Console().WriteTo.File("logs/github-sync-.log", rollingInterval: RollingInterval.Day));
builder.Services.AddFactoryTelemetry(builder.Configuration, "Factory.GitHubSync");
builder.Services.AddFactoryInfrastructure(builder.Configuration);
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
