namespace Factory.Core.Tests;

/// <summary>Covers the character-set check that guards SF-711's runtime "add a repository" API before a value
/// ever reaches the database or gets embedded in a clone URL.</summary>
public sealed class RepositoryNameValidatorTests
{
    [Theory]
    [InlineData("iradulovic")]
    [InlineData("software-factory")]
    [InlineData("my.repo_name")]
    [InlineData("a")]
    [InlineData("A1")]
    public void A_well_formed_owner_or_repo_name_is_valid(string value)
    {
        Assert.True(RepositoryNameValidator.IsValid(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("owner/name")]
    [InlineData("has spaces")]
    [InlineData("semi;colon")]
    public void A_malformed_or_empty_value_is_not_valid(string? value)
    {
        Assert.False(RepositoryNameValidator.IsValid(value));
    }
}
