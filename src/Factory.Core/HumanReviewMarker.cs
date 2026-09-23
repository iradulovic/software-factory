namespace Factory.Core;

/// <summary>Whether a GitHub issue opts its task out of SF-709's default automatic merge, by carrying an explicit
/// "human review" marker in its title, body, or labels. Checked once, at <c>PreparePublicationStep</c> time,
/// against the issue as it existed then — never re-checked afterward, so editing an issue's title, body, or
/// labels after its task has already reached <see cref="FactoryTaskStatus.Published"/> cannot retroactively
/// change that task's already-decided merge policy.</summary>
public static class HumanReviewMarker
{
    private const string Phrase = "human review";
    private const string Label = "human-review";

    public static bool IsPresent(GitHubIssue? issue) =>
        issue is not null && (
            issue.Title.Contains(Phrase, StringComparison.OrdinalIgnoreCase) ||
            issue.Body.Contains(Phrase, StringComparison.OrdinalIgnoreCase) ||
            issue.Labels.Any(label => label.Equals(Label, StringComparison.OrdinalIgnoreCase)));
}
