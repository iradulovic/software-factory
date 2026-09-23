namespace Factory.Core.Tests;

/// <summary>Covers the pure "what does this issue body's own Depends on/Blocked by convention declare" parser (SF-710).</summary>
public sealed class IssueDependencyParserTests
{
    [Fact]
    public void An_empty_body_has_no_references()
    {
        Assert.Empty(IssueDependencyParser.Parse(""));
        Assert.Empty(IssueDependencyParser.Parse(null));
    }

    [Fact]
    public void Ordinary_prose_mentioning_an_issue_number_is_not_a_dependency()
    {
        Assert.Empty(IssueDependencyParser.Parse("See #42 for background on this."));
    }

    [Theory]
    [InlineData("Depends on #55")]
    [InlineData("depends on #55")]
    [InlineData("DEPENDS ON #55")]
    [InlineData("Blocked by #55")]
    [InlineData("blocked by #55")]
    [InlineData("Depends on: #55")]
    [InlineData("- Depends on #55")]
    [InlineData("Depends on #55.")]
    [InlineData("  Depends on #55  ")]
    public void A_same_repository_reference_is_parsed(string line)
    {
        var refs = IssueDependencyParser.Parse($"Some context.\n{line}\nMore text.");
        var single = Assert.Single(refs);
        Assert.Null(single.Owner);
        Assert.Null(single.Name);
        Assert.Equal(55, single.IssueNumber);
    }

    [Fact]
    public void A_cross_repository_reference_is_parsed()
    {
        var refs = IssueDependencyParser.Parse("Depends on iradulovic/other-repo#12");
        var single = Assert.Single(refs);
        Assert.Equal("iradulovic", single.Owner);
        Assert.Equal("other-repo", single.Name);
        Assert.Equal(12, single.IssueNumber);
    }

    [Fact]
    public void Multiple_references_are_all_parsed()
    {
        var refs = IssueDependencyParser.Parse("Depends on #1\nSome text.\nBlocked by owner/repo#2\n");
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r is { Owner: null, IssueNumber: 1 });
        Assert.Contains(refs, r => r is { Owner: "owner", Name: "repo", IssueNumber: 2 });
    }

    [Fact]
    public void Duplicate_references_are_deduplicated()
    {
        var refs = IssueDependencyParser.Parse("Depends on #7\nBlocked by #7\n");
        Assert.Single(refs);
    }
}
