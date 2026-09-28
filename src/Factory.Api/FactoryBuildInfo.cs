using System.Globalization;
using System.Reflection;

namespace Factory.Api;

public sealed record FactoryBuildInfo(string ProductVersion, string? SourceCommit, DateTimeOffset? BuildTimeUtc, string State)
{
    public static FactoryBuildInfo FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);

        return FromMetadata(
            metadata.GetValueOrDefault("Factory.ProductVersion"),
            metadata.GetValueOrDefault("Factory.SourceCommit"),
            metadata.GetValueOrDefault("Factory.BuildTimeUtc"),
            metadata.GetValueOrDefault("Factory.BuildState"));
    }

    public static FactoryBuildInfo FromMetadata(string? productVersion, string? sourceCommit, string? buildTimeUtc, string? state)
    {
        var normalizedState = state?.Trim().ToLowerInvariant();
        var commit = string.IsNullOrWhiteSpace(sourceCommit) || string.Equals(sourceCommit, "unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : sourceCommit.Trim();
        var isKnownState = normalizedState is "development" or "release";
        if (!isKnownState || normalizedState == "release" && (commit is null || string.IsNullOrWhiteSpace(productVersion)))
            return new FactoryBuildInfo("Unknown", commit, ParseBuildTime(buildTimeUtc), "unknown");

        var displayVersion = normalizedState == "development"
            ? "Development"
            : productVersion!.Trim();
        return new FactoryBuildInfo(displayVersion, commit, ParseBuildTime(buildTimeUtc), normalizedState!);
    }

    private static DateTimeOffset? ParseBuildTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
