using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Validates a GitHub owner or repository name before it can be added as a factory-tracked repository
/// (SF-711) — the same character set GitHub itself allows for an owner/repo path segment — so a malformed value
/// never reaches <see cref="IGitHubStore.AddRepositoryAsync"/> or gets embedded in a clone URL.</summary>
public static partial class RepositoryNameValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$")]
    private static partial Regex NamePattern();

    public static bool IsValid(string? value) => value is not null && NamePattern().IsMatch(value);
}
