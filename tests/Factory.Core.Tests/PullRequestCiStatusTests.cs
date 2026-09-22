namespace Factory.Core.Tests;

/// <summary>Covers the pure overall-status derivation (SF-614): the one place a set of individual CI checks
/// (or a read failure) is turned into a single status the dashboard shows.</summary>
public sealed class PullRequestCiStatusTests
{
    [Fact]
    public void A_failed_read_is_unavailable_regardless_of_any_check_data()
    {
        var result = new PullRequestChecksResult(false, null, [new PullRequestCheck("build", PullRequestCiStatus.Success, null)], "gh: authentication required");
        Assert.Equal(PullRequestCiStatus.Unavailable, PullRequestCiStatus.Overall(result));
    }

    [Fact]
    public void No_checks_configured_is_reported_distinctly_from_unavailable_or_success()
    {
        var result = new PullRequestChecksResult(true, "abc123", [], null);
        Assert.Equal(PullRequestCiStatus.NoChecks, PullRequestCiStatus.Overall(result));
    }

    [Fact]
    public void Any_failing_check_makes_the_overall_status_failure_even_alongside_passing_ones()
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("build", PullRequestCiStatus.Success, null), new PullRequestCheck("test", PullRequestCiStatus.Failure, null)], null);
        Assert.Equal(PullRequestCiStatus.Failure, PullRequestCiStatus.Overall(result));
    }

    [Fact]
    public void A_pending_check_with_no_failures_makes_the_overall_status_pending()
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("build", PullRequestCiStatus.Success, null), new PullRequestCheck("test", PullRequestCiStatus.Pending, null)], null);
        Assert.Equal(PullRequestCiStatus.Pending, PullRequestCiStatus.Overall(result));
    }

    [Fact]
    public void Every_check_succeeding_makes_the_overall_status_success()
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("build", PullRequestCiStatus.Success, null), new PullRequestCheck("test", PullRequestCiStatus.Success, null)], null);
        Assert.Equal(PullRequestCiStatus.Success, PullRequestCiStatus.Overall(result));
    }
}
