namespace Factory.Core.Tests;

public sealed class WorktreeCleanupPolicyTests
{
    [Theory]
    [InlineData(FactoryTaskStatus.Completed)]
    [InlineData(FactoryTaskStatus.Cancelled)]
    [InlineData(FactoryTaskStatus.Rejected)]
    public void Resting_statuses_not_retained_are_eligible(FactoryTaskStatus status) =>
        Assert.True(WorktreeCleanupPolicy.IsEligibleForCleanup(status, []));

    [Theory]
    [InlineData(FactoryTaskStatus.Failed)]
    [InlineData(FactoryTaskStatus.NeedsHuman)]
    public void Failed_and_needs_human_are_retained_by_default(FactoryTaskStatus status) =>
        Assert.True(WorktreeCleanupPolicy.DefaultRetainedStatuses.Contains(status));

    [Theory]
    [InlineData(FactoryTaskStatus.Failed)]
    [InlineData(FactoryTaskStatus.NeedsHuman)]
    public void An_explicitly_retained_status_is_never_eligible_even_though_it_would_otherwise_qualify(FactoryTaskStatus status) =>
        Assert.False(WorktreeCleanupPolicy.IsEligibleForCleanup(status, [status]));

    [Theory]
    [InlineData(FactoryTaskStatus.Pending)]
    [InlineData(FactoryTaskStatus.Claimed)]
    [InlineData(FactoryTaskStatus.Preparing)]
    [InlineData(FactoryTaskStatus.Planning)]
    [InlineData(FactoryTaskStatus.Implementing)]
    [InlineData(FactoryTaskStatus.Validating)]
    [InlineData(FactoryTaskStatus.Reviewing)]
    [InlineData(FactoryTaskStatus.ReadyForPublish)]
    [InlineData(FactoryTaskStatus.Published)]
    [InlineData(FactoryTaskStatus.WaitingForQuota)]
    public void Active_or_soon_to_resume_statuses_are_never_eligible_regardless_of_retention_policy(FactoryTaskStatus status) =>
        Assert.False(WorktreeCleanupPolicy.IsEligibleForCleanup(status, []));

    [Fact]
    public void A_path_inside_the_worktrees_root_is_safe()
    {
        Assert.True(WorktreeCleanupPolicy.IsWithinWorktreesRoot("/data/factory", "/data/factory/worktrees/acme/billing/issue-42"));
    }

    [Theory]
    [InlineData("/data/factory-other/worktrees/acme/billing/issue-42")]
    [InlineData("/data/factory/repositories/acme/billing.git")]
    [InlineData("/etc/passwd")]
    [InlineData("/data/factoryevil/worktrees/x")]
    [InlineData("")]
    public void A_path_outside_the_worktrees_root_is_never_safe(string candidate) =>
        Assert.False(WorktreeCleanupPolicy.IsWithinWorktreesRoot("/data/factory", candidate));

    [Fact]
    public void Path_traversal_out_of_the_worktrees_root_is_rejected()
    {
        Assert.False(WorktreeCleanupPolicy.IsWithinWorktreesRoot("/data/factory", "/data/factory/worktrees/../../etc/passwd"));
    }

    [Fact]
    public void A_path_inside_the_repositories_root_is_safe_but_not_inside_the_worktrees_root()
    {
        var candidate = "/data/factory/repositories/acme/billing.git";
        Assert.True(WorktreeCleanupPolicy.IsWithinRepositoriesRoot("/data/factory", candidate));
        Assert.False(WorktreeCleanupPolicy.IsWithinWorktreesRoot("/data/factory", candidate));
    }
}
