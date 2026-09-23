using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Parses a GitHub issue body's own <c>Depends on #N</c> / <c>Blocked by #N</c> convention (SF-710) —
/// the issue-declared analogue of this repository's own <c>Dependencies:</c> tracker line. Deliberately narrow:
/// only a line consisting of that phrase and a reference is recognized, so ordinary prose mentioning an issue
/// number (e.g. "see #42 for background") is never mistaken for a dependency declaration.</summary>
public static partial class IssueDependencyParser
{
    [GeneratedRegex(
        """^\s*(?:[-*]\s+)?(?:depends\s+on|blocked\s+by)\s*:?\s*(?:(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+))?#(?<number>\d+)\s*[.,]?\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DependencyLine();

    public static IReadOnlyList<IssueDependencyRef> Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];
        var results = new List<IssueDependencyRef>();
        foreach (Match match in DependencyLine().Matches(body))
        {
            var owner = match.Groups["owner"].Success ? match.Groups["owner"].Value : null;
            var repo = match.Groups["repo"].Success ? match.Groups["repo"].Value : null;
            results.Add(new IssueDependencyRef(owner, repo, int.Parse(match.Groups["number"].Value)));
        }
        return results.Distinct().ToList();
    }
}
