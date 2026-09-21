using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator.Tests;

public sealed class WorktreeCleanupExecutorTests
{
    [Fact]
    public async Task Removes_an_eligible_resting_tasks_worktree()
    {
        var taskId = Guid.NewGuid();
        var store = new FakeTaskStore();
        store.WorktreeCleanupCandidates.Add(new WorktreeCleanupCandidate(taskId, FactoryTaskStatus.Completed, "/factory/worktrees/acme/billing/issue-1", "acme", "billing"));
        var worktrees = new FakeWorktreeManager();

        var removed = await Executor(store, worktrees).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Single(worktrees.Removed, r => r.Owner == "acme" && r.Name == "billing" && r.WorktreePath == "/factory/worktrees/acme/billing/issue-1");
        Assert.Single(store.ClearedWorkspaces, c => c.TaskId == taskId && c.ExpectedStatus == FactoryTaskStatus.Completed);
    }

    [Theory]
    [InlineData(FactoryTaskStatus.Failed)]
    [InlineData(FactoryTaskStatus.NeedsHuman)]
    public async Task Does_not_remove_a_retained_statuses_worktree_by_default(FactoryTaskStatus status)
    {
        var store = new FakeTaskStore();
        store.WorktreeCleanupCandidates.Add(new WorktreeCleanupCandidate(Guid.NewGuid(), status, "/factory/worktrees/acme/billing/issue-1", "acme", "billing"));
        var worktrees = new FakeWorktreeManager();

        var removed = await Executor(store, worktrees).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Empty(worktrees.Removed);
        Assert.Empty(store.ClearedWorkspaces);
    }

    [Fact]
    public async Task A_retained_status_can_be_configured_to_also_be_cleaned_up()
    {
        var store = new FakeTaskStore();
        store.WorktreeCleanupCandidates.Add(new WorktreeCleanupCandidate(Guid.NewGuid(), FactoryTaskStatus.Failed, "/factory/worktrees/acme/billing/issue-1", "acme", "billing"));
        var worktrees = new FakeWorktreeManager();

        var removed = await Executor(store, worktrees, retainStatuses: []).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Single(worktrees.Removed);
    }

    [Fact]
    public async Task Refuses_to_remove_a_path_outside_the_configured_worktrees_root()
    {
        var store = new FakeTaskStore();
        store.WorktreeCleanupCandidates.Add(new WorktreeCleanupCandidate(Guid.NewGuid(), FactoryTaskStatus.Completed, "/somewhere/else/entirely", "acme", "billing"));
        var worktrees = new FakeWorktreeManager();

        var removed = await Executor(store, worktrees).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Empty(worktrees.Removed);
        Assert.Empty(store.ClearedWorkspaces);
    }

    [Fact]
    public async Task Does_not_touch_disk_when_the_tasks_status_changed_before_it_could_be_cleared()
    {
        var store = new FakeTaskStore { NextClearWorkspaceSucceeds = false };
        store.WorktreeCleanupCandidates.Add(new WorktreeCleanupCandidate(Guid.NewGuid(), FactoryTaskStatus.Completed, "/factory/worktrees/acme/billing/issue-1", "acme", "billing"));
        var worktrees = new FakeWorktreeManager();

        var removed = await Executor(store, worktrees).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.Empty(worktrees.Removed);
        Assert.Single(store.ClearedWorkspaces);
    }

    [Fact]
    public async Task Continues_past_a_single_failure_and_still_removes_the_rest()
    {
        var okTaskId = Guid.NewGuid();
        var store = new FakeTaskStore();
        store.WorktreeCleanupCandidates.Add(new WorktreeCleanupCandidate(Guid.NewGuid(), FactoryTaskStatus.Completed, "/factory/worktrees/acme/billing/issue-1", "acme", "billing"));
        store.WorktreeCleanupCandidates.Add(new WorktreeCleanupCandidate(okTaskId, FactoryTaskStatus.Cancelled, "/factory/worktrees/acme/billing/issue-2", "acme", "billing"));
        var worktrees = new FakeWorktreeManager { FailFirstRemoval = true };

        var removed = await Executor(store, worktrees).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Single(worktrees.Removed, r => r.WorktreePath == "/factory/worktrees/acme/billing/issue-2");
    }

    private static WorktreeCleanupExecutor Executor(FakeTaskStore store, FakeWorktreeManager worktrees, IReadOnlyList<FactoryTaskStatus>? retainStatuses = null) => new(
        store, worktrees,
        Options.Create(new FactoryOptions { RootDirectory = "/factory" }),
        Options.Create(new WorktreeCleanupOptions { RetainStatuses = [.. retainStatuses ?? WorktreeCleanupPolicy.DefaultRetainedStatuses] }),
        NullLogger<WorktreeCleanupExecutor>.Instance);

    private sealed class FakeWorktreeManager : IWorktreeManager
    {
        public List<(string Owner, string Name, string WorktreePath)> Removed { get; } = [];
        public bool FailFirstRemoval { get; set; }
        private bool failedOnce;

        public WorktreeLocation GetLocation(GitHubRepository repository, FactoryTask task) => throw new NotSupportedException();
        public Task<WorktreeLocation> CreateAsync(GitHubRepository repository, FactoryTask task, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveAsync(string owner, string name, string worktreePath, CancellationToken cancellationToken)
        {
            if (FailFirstRemoval && !failedOnce) { failedOnce = true; throw new InvalidOperationException("simulated removal failure"); }
            Removed.Add((owner, name, worktreePath));
            return Task.CompletedTask;
        }
    }
}
