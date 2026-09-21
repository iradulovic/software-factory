namespace Factory.Core;

/// <summary>
/// Decides which resting tasks' worktrees are safe to remove. A task in an active status (still claimed,
/// executing, awaiting publication, or waiting for quota to resume) is never a candidate at all; cleanup only
/// ever considers a task once it has come to rest, and even then only deletes worktrees under the factory's own
/// configured worktrees directory.
/// </summary>
public static class WorktreeCleanupPolicy
{
    /// <summary>Statuses cleanup ever considers. Every other status is active, still executing, or about to
    /// resume automatically (<see cref="FactoryTaskStatus.WaitingForQuota"/>), and must never be touched.</summary>
    public static readonly IReadOnlyList<FactoryTaskStatus> EligibleStatuses =
    [
        FactoryTaskStatus.Completed, FactoryTaskStatus.Failed, FactoryTaskStatus.Cancelled,
        FactoryTaskStatus.Rejected, FactoryTaskStatus.NeedsHuman
    ];

    /// <summary>Retained by default so a human can still inspect what an agent left behind, or why it needed help,
    /// before the worktree disappears.</summary>
    public static readonly IReadOnlyList<FactoryTaskStatus> DefaultRetainedStatuses =
        [FactoryTaskStatus.Failed, FactoryTaskStatus.NeedsHuman];

    public static bool IsEligibleForCleanup(FactoryTaskStatus status, IReadOnlyCollection<FactoryTaskStatus> retainStatuses) =>
        EligibleStatuses.Contains(status) && !retainStatuses.Contains(status);

    /// <summary>A worktree path is only ever safe to delete when it resolves to somewhere inside the factory's own
    /// configured worktrees directory — never anywhere else on disk, regardless of what a task's database row
    /// happens to say.</summary>
    public static bool IsWithinWorktreesRoot(string factoryRootDirectory, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath)) return false;
        var boundary = Path.GetFullPath(Path.Combine(factoryRootDirectory, "worktrees")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidatePath) + Path.DirectorySeparatorChar;
        return full.StartsWith(boundary, StringComparison.Ordinal);
    }

    /// <summary>The same containment check as <see cref="IsWithinWorktreesRoot"/>, for the shared repository cache
    /// rather than a per-task worktree.</summary>
    public static bool IsWithinRepositoriesRoot(string factoryRootDirectory, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath)) return false;
        var boundary = Path.GetFullPath(Path.Combine(factoryRootDirectory, "repositories")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidatePath) + Path.DirectorySeparatorChar;
        return full.StartsWith(boundary, StringComparison.Ordinal);
    }
}
