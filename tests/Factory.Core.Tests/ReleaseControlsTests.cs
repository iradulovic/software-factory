using Factory.Core;

namespace Factory.Core.Tests;

public sealed class ReleaseControlsTests
{
    private static readonly ManualMergeRequest Request = new(Guid.NewGuid(), Guid.NewGuid(), "acme", "billing", 17, "factory/task", "head1");
    private static readonly PullRequestMergeResult Clean = new(true, true, "head1", "base1", "MERGEABLE", "CLEAN", null, "factory/task");
    private static readonly PullRequestChecksResult Green = new(true, "head1", [new PullRequestCheck("build", PullRequestCiStatus.Success, null)], null);

    [Fact]
    public void Manual_merge_requires_the_validated_head_and_green_CI()
    {
        Assert.Null(ManualMergeGuard.Refusal(Request, Clean, Green));
        Assert.Contains("factory-validated", ManualMergeGuard.Refusal(Request, Clean with { HeadSha = "other" }, Green));
        Assert.Contains("head changed", ManualMergeGuard.Refusal(Request, Clean, Green with { HeadSha = "other" }));
        Assert.Contains("Pending", ManualMergeGuard.Refusal(Request, Clean, Green with { Checks = [new PullRequestCheck("build", PullRequestCiStatus.Pending, null)] }));
        Assert.Contains("NoChecks", ManualMergeGuard.Refusal(Request, Clean, Green with { Checks = [] }));
        Assert.Contains("Conflict", ManualMergeGuard.Refusal(Request, Clean with { Mergeable = "CONFLICTING", MergeStateStatus = "DIRTY" }, Green));
        Assert.Contains("read failed", ManualMergeGuard.Refusal(Request, Clean with { Succeeded = false, Error = "network" }, Green));
        Assert.Contains("no longer open", ManualMergeGuard.Refusal(Request, Clean with { Open = false }, Green));
    }

    [Fact]
    public void Draft_human_review_PR_can_be_marked_ready_after_other_guards_pass()
    {
        Assert.Null(ManualMergeGuard.Refusal(Request, Clean with { IsDraft = true, MergeStateStatus = "DRAFT" }, Green));
    }

    [Fact]
    public void A_repair_pause_can_be_requested_during_execution_and_while_waiting()
    {
        Assert.True(TaskStateMachine.CanPauseRepairs(FactoryTaskStatus.Implementing));
        Assert.True(TaskStateMachine.CanPauseRepairs(FactoryTaskStatus.Pending));
        Assert.True(TaskStateMachine.CanPauseRepairs(FactoryTaskStatus.Published));
        Assert.False(TaskStateMachine.CanPauseRepairs(FactoryTaskStatus.Completed));
    }

    [Fact]
    public void Unknown_mergeability_never_becomes_a_conflict()
    {
        var pending = new PullRequestMergeResult(true, true, "head", "base", "UNKNOWN", "UNKNOWN", null);
        var conflict = pending with { Mergeable = "CONFLICTING", MergeStateStatus = "DIRTY" };
        Assert.Equal("Pending", pending.Status);
        Assert.Equal("Conflict", conflict.Status);
    }
}
