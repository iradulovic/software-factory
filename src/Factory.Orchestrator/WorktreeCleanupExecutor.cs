using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Removes worktrees for resting tasks, under <see cref="WorktreeCleanupPolicy"/>: never a task that is still
/// active, never a path outside the factory's own configured worktrees directory, and re-checked against the
/// task's current status immediately before deletion so a task that resumed in the meantime is left untouched.
/// </summary>
public sealed class WorktreeCleanupExecutor(ITaskStore tasks, IWorktreeManager worktrees,
    IOptions<FactoryOptions> options, IOptions<WorktreeCleanupOptions> cleanupOptions, ILogger<WorktreeCleanupExecutor> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var retain = cleanupOptions.Value.RetainStatuses.ToHashSet();
        var candidates = await tasks.GetWorktreeCleanupCandidatesAsync(cancellationToken);
        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (!WorktreeCleanupPolicy.IsEligibleForCleanup(candidate.Status, retain)) continue;
            try
            {
                if (await CleanupOneAsync(candidate, cancellationToken)) removed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to clean up the worktree for task {TaskId}", candidate.TaskId);
            }
        }
        return removed;
    }

    private async Task<bool> CleanupOneAsync(WorktreeCleanupCandidate candidate, CancellationToken cancellationToken)
    {
        if (!WorktreeCleanupPolicy.IsWithinWorktreesRoot(options.Value.RootDirectory, candidate.WorktreePath))
        {
            logger.LogWarning("Refusing to clean up the worktree for task {TaskId}: {Path} is not inside the configured worktrees directory",
                candidate.TaskId, candidate.WorktreePath);
            return false;
        }

        // Cleared before touching disk, not after: a concurrent retry that finds worktree_path already null takes
        // the "create fresh worktree" path instead of expecting one at a location this sweep is about to delete.
        if (!await tasks.ClearWorkspaceIfStatusUnchangedAsync(candidate.TaskId, candidate.Status, cancellationToken))
        {
            logger.LogInformation("Skipped cleaning up the worktree for task {TaskId}: its status changed", candidate.TaskId);
            return false;
        }

        await worktrees.RemoveAsync(candidate.RepositoryOwner, candidate.RepositoryName, candidate.WorktreePath, cancellationToken);
        logger.LogInformation("Cleaned up the worktree for task {TaskId} at {Path}", candidate.TaskId, candidate.WorktreePath);
        return true;
    }
}
