using Factory.Core;

namespace Factory.Core.Tests;

public sealed class SemanticReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("0.0.0", 0, 0, 0)]
    public void Try_parse_accepts_three_nonnegative_numeric_components(string value, int major, int minor, int patch)
    {
        Assert.True(SemanticReleaseVersion.TryParse(value, out var version));
        Assert.Equal(new SemanticReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("2.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3.4")]
    public void Try_parse_rejects_legacy_tags_prereleases_and_non_semver_text(string value)
    {
        Assert.False(SemanticReleaseVersion.TryParse(value, out _));
    }

    [Fact]
    public void Next_returns_patch_minor_and_major_versions()
    {
        var version = new SemanticReleaseVersion(1, 2, 3);

        Assert.Equal("1.2.4", version.Next(ReleaseVersionChange.BugFixes).ToString());
        Assert.Equal("1.3.0", version.Next(ReleaseVersionChange.NewFeatures).ToString());
        Assert.Equal("2.0.0", version.Next(ReleaseVersionChange.BreakingChanges).ToString());
    }

    [Fact]
    public void Try_parse_tag_requires_the_repository_tag_prefix()
    {
        Assert.True(SemanticReleaseVersion.TryParseTag("v1.2.3", "v", out var version));
        Assert.Equal("1.2.3", version.ToString());
        Assert.False(SemanticReleaseVersion.TryParseTag("1.2.3", "v", out _));
    }
}
