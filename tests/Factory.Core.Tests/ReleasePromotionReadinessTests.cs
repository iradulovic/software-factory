using Factory.Core;

namespace Factory.Core.Tests;

public sealed class ReleasePromotionReadinessTests
{
    [Fact]
    public void Completed_issue_with_recorded_pull_request_and_green_ci_is_ready()
    {
        var release = Release(new FactoryReleaseIssue(10, 12, "Add export", "OPEN", false, "Completed",
            Guid.NewGuid(), "release/2.4", null, "Success", 31, "https://github.com/acme/billing/pull/31"));
        var issue = release.Issues[0] with { TaskReleaseId = release.Id };

        var result = ReleasePromotionReadiness.Evaluate(release with { Issues = [issue] });

        Assert.True(result.Ready);
        Assert.Equal(PullRequestCiStatus.Success, result.CiStatus);
        Assert.Empty(result.RemainingIssues);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void Incomplete_or_failed_ci_issue_is_reported_as_a_blocker()
    {
        var release = Release(
            new FactoryReleaseIssue(10, 12, "Add export", "OPEN", true, null),
            new FactoryReleaseIssue(11, 13, "Add audit", "OPEN", false, "Completed", Guid.NewGuid(),
                "release/2.4", null, "Failure", 32, "https://github.com/acme/billing/pull/32"));
        var issues = release.Issues.Select(issue => issue.TaskStatus is null
            ? issue
            : issue with { TaskReleaseId = release.Id }).ToArray();

        var result = ReleasePromotionReadiness.Evaluate(release with { Issues = issues });

        Assert.False(result.Ready);
        Assert.Equal(["#12 · Add export (no factory task)"], result.RemainingIssues);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("pull request CI is failure", StringComparison.Ordinal));
        Assert.Equal(PullRequestCiStatus.Failure, result.CiStatus);
    }

    [Fact]
    public void Completed_task_from_another_release_branch_blocks_promotion()
    {
        var release = Release(new FactoryReleaseIssue(10, 12, "Add export", "OPEN", false, "Completed",
            Guid.NewGuid(), "main", Guid.NewGuid(), "Success", 31, "https://github.com/acme/billing/pull/31"));

        var result = ReleasePromotionReadiness.Evaluate(release);

        Assert.False(result.Ready);
        Assert.Contains(result.Blockers, blocker => blocker.Contains("not assigned to this release's integration branch", StringComparison.Ordinal));
    }

    private static FactoryRelease Release(params FactoryReleaseIssue[] issues) =>
        new(Guid.NewGuid(), 1, "acme/billing", "Release", "2.4.0", "release/2.4", "main", null,
            FactoryReleaseStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, null, issues);
}
