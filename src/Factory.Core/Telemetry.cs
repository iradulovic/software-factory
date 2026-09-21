using System.Diagnostics;

namespace Factory.Core;

/// <summary>The single <see cref="ActivitySource"/> every host (API, orchestrator, GitHub sync) emits spans on.
/// Which process emitted a given span is the resource-level "service.name" attribute's job (configured per host
/// by Factory.Infrastructure's telemetry wiring), not a separate ActivitySource per host.</summary>
public static class FactoryTelemetry
{
    public static readonly ActivitySource Source = new("Factory");
}
