namespace Factory.Core.Tests;

/// <summary>Covers the pure "does this issue opt out of SF-709's default automatic merge" check.</summary>
public sealed class HumanReviewMarkerTests
{
    private static GitHubIssue Issue(string title = "Add export", string body = "Implement it.", params string[] labels) =>
        new(1, 1, 100, 42, title, body, "OPEN", "alice", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, labels, []);

    [Fact]
    public void No_issue_is_not_present()
    {
        Assert.False(HumanReviewMarker.IsPresent(null));
    }

    [Fact]
    public void An_ordinary_issue_with_no_marker_is_not_present()
    {
        Assert.False(HumanReviewMarker.IsPresent(Issue()));
    }

    [Theory]
    [InlineData("Please HUMAN REVIEW this before merging")]
    [InlineData("please human review this before merging")]
    public void A_marker_in_the_title_is_present_case_insensitively(string title)
    {
        Assert.True(HumanReviewMarker.IsPresent(Issue(title: title)));
    }

    [Fact]
    public void A_marker_in_the_body_is_present()
    {
        Assert.True(HumanReviewMarker.IsPresent(Issue(body: "This touches billing, HUMAN REVIEW please.")));
    }

    [Fact]
    public void A_human_review_label_is_present_regardless_of_title_or_body_text()
    {
        Assert.True(HumanReviewMarker.IsPresent(Issue(labels: "human-review")));
    }

    [Fact]
    public void A_label_is_matched_case_insensitively()
    {
        Assert.True(HumanReviewMarker.IsPresent(Issue(labels: "Human-Review")));
    }

    [Fact]
    public void An_unrelated_label_does_not_trigger_it()
    {
        Assert.False(HumanReviewMarker.IsPresent(Issue(labels: ["factory:ready", "bug"])));
    }
}
