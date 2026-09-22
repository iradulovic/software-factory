namespace Factory.Core;

public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken); }
public interface IAgentRunner
{
    /// <summary>Matches <see cref="AgentProfile.Name"/> and <see cref="FactoryTask.PreferredAgent"/>.</summary>
    string Name { get; }
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}
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

    /// <summary>Claims the oldest <c>Requested</c> publication, or reclaims a <c>Publishing</c> one whose lease has
    /// expired — the worker that held it crashed somewhere between claiming and recording completion. Push and
    /// pull-request creation are idempotent under retry, so reclaiming and re-running a stranded attempt is always
    /// safe.</summary>
    Task<PublicationRequest?> ClaimNextPublicationAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);
    Task CompletePublicationAsync(Guid publicationId, string status, int? pullRequestNumber, string? pullRequestUrl, string? error, CancellationToken cancellationToken);

    /// <summary>Finishes a task's transition to <see cref="FactoryTaskStatus.Published"/> when its most recent
    /// publication already succeeded (<c>PullRequestCreated</c>) but the task itself is still resting in
    /// <see cref="FactoryTaskStatus.ReadyForPublish"/> — the worker crashed between recording that success and
    /// making this transition. Returns how many tasks were reconciled.</summary>
    Task<int> ReconcilePublishedTasksAsync(CancellationToken cancellationToken);

    /// <summary>Persists the head commit a task's implementation was actually validated against, so publication
    /// can refuse to push a worktree whose HEAD has since moved past what was validated.</summary>
    Task SetValidatedHeadCommitAsync(Guid taskId, string headCommit, CancellationToken cancellationToken);

    /// <summary>Records which agent is actually invoked for a task's current attempt, from the moment it is
    /// selected (before the process starts) until that invocation finishes (pass <see langword="null"/> to
    /// clear it) — the live signal of which provider is really running a task right now, distinct from
    /// <see cref="FactoryTask.PreferredAgent"/> (a preference, not necessarily who ends up invoked after a
    /// fallback) and from <c>factory.agent_run.agent</c> (only recorded after an invocation finishes).</summary>
    Task SetCurrentAgentAsync(Guid taskId, string? agentName, CancellationToken cancellationToken);

    /// <summary>Records one attempt to write to GitHub (a comment or a state-label change) as append-only operational
    /// state, regardless of whether it succeeded.</summary>
    Task RecordGitHubWriteAsync(Guid taskId, string kind, string detail, bool succeeded, string? error, CancellationToken cancellationToken);

    /// <summary>Tasks resting in <see cref="FactoryTaskStatus.Published"/> with a known pull request, for the
    /// GitHub sync worker to observe and resolve to <see cref="FactoryTaskStatus.Completed"/> or <see cref="FactoryTaskStatus.Rejected"/>.</summary>
    Task<IReadOnlyList<PublishedTaskRef>> GetPublishedTasksAsync(CancellationToken cancellationToken);

    /// <summary>How many invocations count toward this task's <see cref="RepositoryConfiguration.MaxImplementationAttempts"/>
    /// budget, across every run — a quota-interrupted invocation (see <see cref="AgentRunRecord.CountsAsImplementationAttempt"/>)
    /// never got a real chance to implement anything, so it is excluded here even though it is still recorded in full.</summary>
    Task<int> CountAgentRunsAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>How many quota-interrupted invocations this task has accumulated, across every run — the separate
    /// bound (<see cref="RepositoryConfiguration.MaxQuotaInterruptions"/>) that keeps excluding them from the
    /// implementation-attempt budget from letting a task wait on quota forever.</summary>
    Task<int> CountQuotaInterruptionsAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>The most recent implementation attempt's outcome for this task, or <see langword="null"/> if
    /// there has not been one yet.</summary>
    Task<PreviousAttemptSummary?> GetPreviousAttemptAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>Resumes the single highest-priority <see cref="FactoryTaskStatus.WaitingForQuota"/> task back to
    /// <see cref="FactoryTaskStatus.Pending"/>, but only if at least one of <paramref name="configuredAgents"/> is
    /// not currently at quota (per <see cref="RecordAgentQuotaStatusAsync"/>) — scheduled from provider
    /// availability, never a waiting task's own invocation history, so a task that was never actually invoked
    /// (every provider was already at quota on its first attempt) is never stuck forever. Resumes at most one
    /// task per call, deliberately: see the implementation for why. Returns how many were resumed (0 or 1).</summary>
    Task<int> ResumeExpiredQuotaTasksAsync(IReadOnlyList<string> configuredAgents, CancellationToken cancellationToken);

    /// <summary>Whether the named agent currently has a recorded quota status whose reset time has not yet
    /// passed. Backed by <see cref="RecordAgentQuotaStatusAsync"/>, independent of any particular task's run.</summary>
    Task<bool> IsAgentAtQuotaAsync(string agent, CancellationToken cancellationToken);

    /// <summary>Persists this agent's current quota status, independent of any particular task or run. Called
    /// after every invocation of the agent, whether or not quota was detected, so a status that cleared is
    /// reflected immediately rather than only by scanning task-run history.</summary>
    Task RecordAgentQuotaStatusAsync(AgentQuotaStatus status, CancellationToken cancellationToken);

    /// <summary>The most recently recorded quota status for an agent, or <see langword="null"/> if none has ever
    /// been recorded.</summary>
    Task<AgentQuotaStatus?> GetAgentQuotaStatusAsync(string agent, CancellationToken cancellationToken);

    /// <summary>Cancels a task's <see cref="FactoryTaskStatus.Pending"/> request for the given GitHub issue
    /// (never a task that is already active), with an explicit reason, because the issue was closed or lost its
    /// <c>factory:ready</c> label on GitHub. Returns whether a task was actually cancelled.</summary>
    Task<bool> CancelPendingForIssueAsync(long issueId, string reason, CancellationToken cancellationToken);

    /// <summary>Records this worker process as alive, and the task it is currently executing, if any. Called on a
    /// heartbeat cadence so the dashboard can show worker status and detect one that has stopped reporting.</summary>
    Task RecordHeartbeatAsync(string workerId, string host, Guid? currentTaskId, CancellationToken cancellationToken);

    /// <summary>Every resting task with a recorded worktree, whatever its status — <see cref="WorktreeCleanupPolicy"/>
    /// decides which of these are actually eligible for cleanup under the configured retention policy.</summary>
    Task<IReadOnlyList<WorktreeCleanupCandidate>> GetWorktreeCleanupCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>Clears a task's recorded worktree and branch, but only if its status is still exactly
    /// <paramref name="expectedStatus"/> — protecting against a task that resumed (e.g. a human retry) between
    /// being listed as a cleanup candidate and actually being cleaned up. Returns whether it was cleared.</summary>
    Task<bool> ClearWorkspaceIfStatusUnchangedAsync(Guid taskId, FactoryTaskStatus expectedStatus, CancellationToken cancellationToken);
}

public interface IGitHubStore
{
    Task<IReadOnlyList<GitHubRepository>> GetEnabledRepositoriesAsync(CancellationToken cancellationToken);
    Task<GitHubRepository?> GetRepositoryAsync(long id, CancellationToken cancellationToken);
    Task<GitHubIssue?> GetIssueAsync(long id, CancellationToken cancellationToken);
    Task UpsertRepositoryAsync(GitHubRepository repository, CancellationToken cancellationToken);

    /// <summary>Records the point in time through which this repository's issues have been fully synchronized, so
    /// the next cycle's <see cref="IGitHubClient.GetIssuesAsync"/> call can search only for what changed since
    /// then. Pass the time the sync cycle started, not when it finished, so an issue updated while this cycle was
    /// still running is safely re-fetched next time rather than silently skipped.</summary>
    Task MarkRepositorySyncedAsync(long repositoryId, DateTimeOffset syncedThrough, CancellationToken cancellationToken);
    Task RecordRepositorySyncFailureAsync(long repositoryId, string error, CancellationToken cancellationToken);
    Task<GitHubIssue> UpsertIssueAsync(long repositoryId, GitHubIssue issue, CancellationToken cancellationToken);
}

public interface IGitHubClient
{
    /// <summary>All issues (open and closed) whose <c>updatedAt</c> is at or after <paramref name="since"/> (or
    /// every issue, if <see langword="null"/>), fully paginated rather than capped at a single page, with every
    /// comment fetched per issue rather than relying on <c>gh issue list</c>'s own nested comments field.</summary>
    Task<IReadOnlyList<GitHubIssue>> GetIssuesAsync(GitHubRepository repository, DateTimeOffset? since, CancellationToken cancellationToken);

    /// <summary>The current state of a pull request the factory opened, or <see langword="null"/> if it could not be read.</summary>
    Task<PullRequestState?> GetPullRequestStateAsync(string owner, string name, int number, CancellationToken cancellationToken);
}
public interface IRepositoryCache
{
    Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken);

    /// <summary>The deterministic cache path for a repository, computed with no I/O — the same path
    /// <see cref="PrepareAsync"/> would prepare, without fetching.</summary>
    string GetPath(string owner, string name);
}
public interface IWorktreeManager
{
    WorktreeLocation GetLocation(GitHubRepository repository, FactoryTask task);
    Task<WorktreeLocation> CreateAsync(GitHubRepository repository, FactoryTask task, CancellationToken cancellationToken);

    /// <summary>Removes a resting task's worktree, orchestrator-owned like its creation. If the worktree directory
    /// is already gone, prunes the cache's stale administrative record instead of failing.</summary>
    Task RemoveAsync(string owner, string name, string worktreePath, CancellationToken cancellationToken);
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
public interface ITaskContextWriter { Task WriteAsync(string worktreePath, GitHubRepository repository, GitHubIssue? issue, FactoryTask task, AttemptContext attempt, CancellationToken cancellationToken); }
public interface IAgentResultReader { Task<(AgentResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken); }

/// <summary>
/// The orchestrator's only path to writing to GitHub. Never pushes to anything but the task's own factory branch,
/// and never merges — merging remains an exclusively human action, performed on GitHub itself.
/// </summary>
public interface IGitHubPublisher
{
    Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken);

    /// <summary>The existing open pull request for this branch, if any — checked before creating a new one so a
    /// retried publication (for example after a crash right after a prior attempt's <c>gh pr create</c> already
    /// succeeded) never creates a duplicate. <see langword="null"/> means none was found; a returned
    /// <see cref="PullRequestResult"/> with <c>Succeeded=false</c> means the check itself failed.</summary>
    Task<PullRequestResult?> FindExistingPullRequestAsync(string owner, string name, string branchName, CancellationToken cancellationToken);

    Task<PullRequestResult> CreatePullRequestAsync(string owner, string name, string branchName, string baseBranch, string title, string body, CancellationToken cancellationToken);

    /// <summary>Posts a comment on the issue backing a task, so people who work in GitHub see what the factory did
    /// without opening the dashboard.</summary>
    Task<GitHubWriteResult> CommentOnIssueAsync(string owner, string name, int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>Sets the one <c>factory:*</c> state label that reflects a task's current outcome, removing whichever
    /// other state label the issue previously carried.</summary>
    Task<GitHubWriteResult> SetStateLabelAsync(string owner, string name, int issueNumber, string label, CancellationToken cancellationToken);
}
