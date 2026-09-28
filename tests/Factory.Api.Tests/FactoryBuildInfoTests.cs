using Factory.Api;
using System.Reflection;

namespace Factory.Api.Tests;

public sealed class FactoryBuildInfoTests
{
    [Fact]
    public void Assembly_informational_version_contains_the_stamped_factory_identity()
    {
        var assembly = typeof(FactoryBuildInfo).Assembly;
        var identity = FactoryBuildInfo.FromAssembly(assembly);
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.Equal($"{identity.ProductVersion}+{identity.SourceCommit ?? "unknown"}", informationalVersion);
    }

    [Fact]
    public void Release_identity_requires_a_source_commit_and_keeps_its_version()
    {
        var info = FactoryBuildInfo.FromMetadata("v1.2.3", new string('a', 40), "2026-09-28T12:00:00Z", "release");

        Assert.Equal("v1.2.3", info.ProductVersion);
        Assert.Equal(new string('a', 40), info.SourceCommit);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T12:00:00Z"), info.BuildTimeUtc);
        Assert.Equal("release", info.State);
    }

    [Fact]
    public void Development_identity_does_not_expose_a_package_or_assembly_version()
    {
        var info = FactoryBuildInfo.FromMetadata("0.1.0", new string('b', 40), null, "development");

        Assert.Equal("Development", info.ProductVersion);
        Assert.Equal(new string('b', 40), info.SourceCommit);
        Assert.Null(info.BuildTimeUtc);
        Assert.Equal("development", info.State);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("v1.2.3", null, null, "release")]
    [InlineData("Development", "unknown", "not a timestamp", "unknown")]
    public void Missing_or_invalid_metadata_is_reported_as_unknown(string? version, string? commit, string? buildTime, string? state)
    {
        var info = FactoryBuildInfo.FromMetadata(version, commit, buildTime, state);

        Assert.Equal("Unknown", info.ProductVersion);
        Assert.Null(info.SourceCommit);
        Assert.Null(info.BuildTimeUtc);
        Assert.Equal("unknown", info.State);
    }
}
