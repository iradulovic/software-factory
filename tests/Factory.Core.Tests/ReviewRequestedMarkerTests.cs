namespace Factory.Core.Tests;

/// <summary>Covers the pure "does this issue opt into SF-702's second-agent review pass" check.</summary>
public sealed class ReviewRequestedMarkerTests
{
    private static GitHubIssue Issue(string title = "Add export", string body = "Implement it.", params string[] labels) =>
        new(1, 1, 100, 42, title, body, "OPEN", "alice", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, labels, []);

    [Fact]
    public void No_issue_is_not_present()
    {
        Assert.False(ReviewRequestedMarker.IsPresent(null));
    }

    [Fact]
    public void An_ordinary_issue_with_no_marker_is_not_present()
    {
        Assert.False(ReviewRequestedMarker.IsPresent(Issue()));
    }

    [Theory]
    [InlineData("Please REQUEST REVIEW before publishing")]
    [InlineData("please request review before publishing")]
    public void A_marker_in_the_title_is_present_case_insensitively(string title)
    {
        Assert.True(ReviewRequestedMarker.IsPresent(Issue(title: title)));
    }

    [Fact]
    public void A_marker_in_the_body_is_present()
    {
        Assert.True(ReviewRequestedMarker.IsPresent(Issue(body: "This touches billing, request review please.")));
    }

    [Fact]
    public void A_request_review_label_is_present_regardless_of_title_or_body_text()
    {
        Assert.True(ReviewRequestedMarker.IsPresent(Issue(labels: "request-review")));
    }

    [Fact]
    public void A_label_is_matched_case_insensitively()
    {
        Assert.True(ReviewRequestedMarker.IsPresent(Issue(labels: "Request-Review")));
    }

    [Fact]
    public void An_unrelated_label_does_not_trigger_it()
    {
        Assert.False(ReviewRequestedMarker.IsPresent(Issue(labels: ["factory:ready", "bug"])));
    }
}
