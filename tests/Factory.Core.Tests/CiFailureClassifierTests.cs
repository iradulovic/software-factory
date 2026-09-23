namespace Factory.Core.Tests;

/// <summary>Covers the pure repairable-vs-operational classification of an already-failed CI result (SF-706):
/// the one place that decides whether a failure is worth an automatic repair attempt or should block on a
/// human instead.</summary>
public sealed class CiFailureClassifierTests
{
    [Fact]
    public void A_failed_read_is_operational_since_there_is_no_code_failure_to_look_at()
    {
        var result = new PullRequestChecksResult(false, null, [], "gh: authentication required");
        Assert.Equal(ValidationFailureKind.Operational, CiFailureClassifier.Classify(result));
    }

    [Fact]
    public void No_failing_checks_is_repairable_by_default()
    {
        var result = new PullRequestChecksResult(true, "abc123", [new PullRequestCheck("build", PullRequestCiStatus.Success, null, "SUCCESS")], null);
        Assert.Equal(ValidationFailureKind.Repairable, CiFailureClassifier.Classify(result));
    }

    [Fact]
    public void A_genuine_test_failure_is_repairable()
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("test", PullRequestCiStatus.Failure, null, "FAILURE")], null);
        Assert.Equal(ValidationFailureKind.Repairable, CiFailureClassifier.Classify(result));
    }

    [Fact]
    public void A_legacy_commit_status_error_is_operational()
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("legacy-ci", PullRequestCiStatus.Failure, null, "ERROR")], null);
        Assert.Equal(ValidationFailureKind.Operational, CiFailureClassifier.Classify(result));
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("TIMED_OUT")]
    [InlineData("ACTION_REQUIRED")]
    [InlineData("STARTUP_FAILURE")]
    [InlineData("STALE")]
    public void A_check_that_never_genuinely_ran_the_code_is_operational(string rawState)
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("build", PullRequestCiStatus.Failure, null, rawState)], null);
        Assert.Equal(ValidationFailureKind.Operational, CiFailureClassifier.Classify(result));
    }

    [Fact]
    public void One_genuine_failure_alongside_an_operational_one_is_still_repairable()
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("build", PullRequestCiStatus.Failure, null, "CANCELLED"),
             new PullRequestCheck("test", PullRequestCiStatus.Failure, null, "FAILURE")], null);
        Assert.Equal(ValidationFailureKind.Repairable, CiFailureClassifier.Classify(result));
    }

    [Fact]
    public void A_failing_check_with_no_raw_state_defaults_to_repairable_rather_than_silently_blocking()
    {
        var result = new PullRequestChecksResult(true, "abc123",
            [new PullRequestCheck("build", PullRequestCiStatus.Failure, null)], null);
        Assert.Equal(ValidationFailureKind.Repairable, CiFailureClassifier.Classify(result));
    }
}
