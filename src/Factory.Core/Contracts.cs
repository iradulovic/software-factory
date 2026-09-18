namespace Factory.Core;

public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken); }
public interface IAgentRunner { Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken); }
public interface IAgentAvailabilityChecker
{
    string Agent { get; }
    Task<AgentAvailability> CheckAsync(CancellationToken cancellationToken);
}

public interface ITaskStore
{
    Task<FactoryTask?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);
    Task<bool> RenewLeaseAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken cancellationToken);
    Task ReleaseLeaseAsync(Guid taskId, string workerId, CancellationToken cancellationToken);
    Task<bool> CreateForIssueIfEligibleAsync(GitHubIssue issue, string baseBranch, CancellationToken cancellationToken);
    Task TransitionAsync(Guid taskId, FactoryTaskStatus expected, FactoryTaskStatus next, string? failureReason, CancellationToken cancellationToken);
    Task<bool> RetryAsync(Guid taskId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid taskId, CancellationToken cancellationToken);
    Task SetWorkspaceAsync(Guid taskId, string branchName, string worktreePath, CancellationToken cancellationToken);
    Task<Guid> StartRunAsync(Guid taskId, string workerId, CancellationToken cancellationToken);
    Task<Guid> StartStepAsync(Guid runId, string stepType, int attempt, CancellationToken cancellationToken);
    Task CompleteStepAsync(Guid stepId, ExecutionStatus status, string? error, string? output, CancellationToken cancellationToken);
    Task SaveAgentRunAsync(AgentRunRecord run, CancellationToken cancellationToken);
    Task CompleteRunAsync(Guid runId, ExecutionStatus status, CancellationToken cancellationToken);
    Task SetRunConfigurationAsync(Guid runId, RepositoryConfiguration configuration, CancellationToken cancellationToken);
    Task CloseExecutionAsync(Guid runId, ExecutionStatus status, string reason, CancellationToken cancellationToken);
    Task SetChangeSummaryAsync(Guid runId, ChangeSummary summary, CancellationToken cancellationToken);

    /// <summary>Requests publication of a validated task. Returns the new publication's id, or <see langword="null"/>
    /// if one is already in flight for this task (an explicit human retry after a failed attempt is still allowed).</summary>
    Task<Guid?> RequestPublicationAsync(Guid taskId, Guid? runId, string requestedBy, CancellationToken cancellationToken);
    Task<PublicationRequest?> ClaimNextPublicationAsync(string workerId, CancellationToken cancellationToken);
    Task CompletePublicationAsync(Guid publicationId, string status, int? pullRequestNumber, string? pullRequestUrl, string? error, CancellationToken cancellationToken);

    /// <summary>Records one attempt to write to GitHub (a comment or a state-label change) as append-only operational
    /// state, regardless of whether it succeeded.</summary>
    Task RecordGitHubWriteAsync(Guid taskId, string kind, string detail, bool succeeded, string? error, CancellationToken cancellationToken);

    /// <summary>Tasks resting in <see cref="FactoryTaskStatus.Published"/> with a known pull request, for the
    /// GitHub sync worker to observe and resolve to <see cref="FactoryTaskStatus.Completed"/> or <see cref="FactoryTaskStatus.Rejected"/>.</summary>
    Task<IReadOnlyList<PublishedTaskRef>> GetPublishedTasksAsync(CancellationToken cancellationToken);
}

public interface IGitHubStore
{
    Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken);
    Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken);
    Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken);
    Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken);
    Task MarkRepositorySyncedAsync(long repositoryId, CancellationToken cancellationToken);
    Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken);
    Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken);
}

public interface IGitHubClient
{
    Task<IReadOnlyList<GitHubIssue>> GetOpenIssuesAsync(GitHubRepository repository, CancellationToken cancellationToken);

    /// <summary>The current state of a pull request the factory opened, or <see langword="null"/> if it could not be read.</summary>
    Task<PullRequestState?> GetPullRequestStateAsync(string owner, string name, int number, CancellationToken cancellationToken);
}
public interface IRepositoryCache { Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken); }
public interface IWorktreeManager
{
    WorktreeLocation GetLocation(GitHubRepository repository, FactoryTask task);
    Task<WorktreeLocation> CreateAsync(GitHubRepository repository, FactoryTask task, CancellationToken cancellationToken);
}
public sealed record WorktreeLocation(string BranchName, string Path);
public interface IRepositoryConfigurationReader { Task<RepositoryConfiguration> ReadAsync(string worktreePath, string baseRef, CancellationToken cancellationToken); }
public interface IWorktreeInspector
{
    Task<bool> HasChangesAsync(string worktreePath, string baseRef, CancellationToken cancellationToken);
    Task<ChangeSummary> SummarizeAsync(string worktreePath, string baseRef, CancellationToken cancellationToken);
}

/// <summary>
/// The orchestrator's own, independently computed account of what a task changed, as it will be published.
/// <see cref="BaseCommit"/> is the merge base between the worktree's branch and <c>baseRef</c>, not <c>baseRef</c>'s
/// current tip, so a concurrent fetch that advances the base branch for another task never skews this task's diff.
/// </summary>
public sealed record ChangeSummary(
    bool IsClean,
    string CurrentBranch,
    string BaseCommit,
    string HeadCommit,
    IReadOnlyList<string> FilesChanged,
    int LinesAdded,
    int LinesRemoved);
public interface ITaskContextWriter { Task WriteAsync(string worktreePath, GitHubRepository repository, GitHubIssue? issue, FactoryTask task, CancellationToken cancellationToken); }
public interface IAgentResultReader { Task<(AgentResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken); }

/// <summary>
/// The orchestrator's only path to writing to GitHub. Never pushes to anything but the task's own factory branch,
/// and never merges — merging remains an exclusively human action, performed on GitHub itself.
/// </summary>
public interface IGitHubPublisher
{
    Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken);
    Task<PullRequestResult> CreatePullRequestAsync(string owner, string name, string branchName, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>Posts a comment on the issue backing a task, so people who work in GitHub see what the factory did
    /// without opening the dashboard.</summary>
    Task<GitHubWriteResult> CommentOnIssueAsync(string owner, string name, int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>Sets the one <c>factory:*</c> state label that reflects a task's current outcome, removing whichever
    /// other state label the issue previously carried.</summary>
    Task<GitHubWriteResult> SetStateLabelAsync(string owner, string name, int issueNumber, string label, CancellationToken cancellationToken);
}
