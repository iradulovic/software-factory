using Factory.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Factory.Infrastructure;

public static class TelemetryExtensions
{
    /// <summary>
    /// Wires OpenTelemetry tracing identically for every host: the service name and OTLP endpoint both come from
    /// the "Telemetry" configuration section (<see cref="TelemetryOptions"/>). Exporting is entirely optional —
    /// with no endpoint configured, no exporter is registered at all, so a missing or unreachable collector never
    /// affects startup.
    /// </summary>
    /// <param name="defaultServiceName">Used unless <see cref="TelemetryOptions.ServiceName"/> overrides it.</param>
    public static IServiceCollection AddFactoryTelemetry(this IServiceCollection services, IConfiguration configuration, string defaultServiceName)
    {
        var options = configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
        var serviceName = ResolveServiceName(options, defaultServiceName);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(FactoryTelemetry.Source.Name);
                if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(options.OtlpEndpoint));
            });

        return services;
    }

    public static string ResolveServiceName(TelemetryOptions options, string defaultServiceName) =>
        string.IsNullOrWhiteSpace(options.ServiceName) ? defaultServiceName : options.ServiceName;
}
