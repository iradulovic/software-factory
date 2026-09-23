namespace Factory.Core;

/// <summary>Whether a GitHub issue opts its task into an additional, bounded second-agent review pass (SF-702)
/// before publish — mirroring <see cref="HumanReviewMarker"/>'s convention (a title/body phrase, or a label),
/// checked once at <c>PreparePublicationStep</c> time against the issue as it existed then. This is one of two
/// ways a task can request review; the other is the implementation agent's own result reporting a risk worth a
/// second look (see <c>PreparePublicationStep</c>). Review stays opt-in either way — never the default for every
/// task.</summary>
public static class ReviewRequestedMarker
{
    private const string Phrase = "request review";
    private const string Label = "request-review";

    public static bool IsPresent(GitHubIssue? issue) =>
        issue is not null && (
            issue.Title.Contains(Phrase, StringComparison.OrdinalIgnoreCase) ||
            issue.Body.Contains(Phrase, StringComparison.OrdinalIgnoreCase) ||
            issue.Labels.Any(label => label.Equals(Label, StringComparison.OrdinalIgnoreCase)));
}
