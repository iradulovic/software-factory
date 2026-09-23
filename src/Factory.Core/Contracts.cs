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

    /// <summary>Records operator feedback (a correction, or a manual-test failure) and, atomically with that
    /// record, returns the task to <see cref="FactoryTaskStatus.Pending"/> for a fresh implementation attempt
    /// that incorporates it (SF-613) — from any resting state a human might reasonably act on (<see cref="FactoryTaskStatus.Failed"/>,
    /// <see cref="FactoryTaskStatus.WaitingForQuota"/>, <see cref="FactoryTaskStatus.NeedsHuman"/>, <see cref="FactoryTaskStatus.Rejected"/>,
    /// <see cref="FactoryTaskStatus.ReadyForPublish"/>, or <see cref="FactoryTaskStatus.Published"/>), never mid-execution.
    /// The existing worktree and branch are reused unchanged, so a subsequent publish updates the same pull
    /// request instead of creating a duplicate one. Every <c>agent_run</c> recorded before this call is excluded
    /// from the next implementation-attempt budget check (see <see cref="CountAgentRunsAsync"/>), so this always
    /// grants a bounded, fresh <see cref="RepositoryConfiguration.MaxImplementationAttempts"/> allowance rather
    /// than either staying permanently exhausted or granting unlimited retries. Returns <see langword="false"/>
    /// if the task does not currently rest in one of the allowed statuses.</summary>
    Task<bool> ContinueWithFeedbackAsync(Guid taskId, string feedback, CancellationToken cancellationToken);

    /// <summary>Every piece of operator feedback recorded for a task, oldest first — permanent and auditable,
    /// even once a later continuation supersedes it (SF-613).</summary>
    Task<IReadOnlyList<TaskFeedback>> GetFeedbackAsync(Guid taskId, CancellationToken cancellationToken);
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

    /// <summary>Persists this task's effective merge policy (SF-709), computed once by <c>PreparePublicationStep</c>
    /// from the repository's <see cref="RepositoryConfiguration.RequireHumanMerge"/> default OR'd with
    /// <see cref="HumanReviewMarker.IsPresent"/> against the task's issue. Read back by publication (to decide a
    /// draft vs. ready-for-review pull request) and by <c>Factory.GitHubSync.Worker</c> (to decide whether a
    /// CI-green pull request merges itself). Never recomputed afterward — see <see cref="HumanReviewMarker"/>.</summary>
    Task SetRequireHumanMergeAsync(Guid taskId, bool requireHumanMerge, CancellationToken cancellationToken);

    /// <summary>Persists whether this task opted into a second-agent review pass (SF-702) — its issue carried
    /// <see cref="ReviewRequestedMarker"/>, or the implementation agent's own result reported at least one risk.
    /// Computed once by <c>PreparePublicationStep</c>, exactly like <see cref="SetRequireHumanMergeAsync"/>, and
    /// read back by the executor to decide whether to run <c>ReviewStep</c> at all: review is opt-in, never run
    /// for every task by default.</summary>
    Task SetReviewRequestedAsync(Guid taskId, bool requested, CancellationToken cancellationToken);

    /// <summary>Persists the findings one review invocation reported (SF-702), one row per finding. An empty
    /// <paramref name="findings"/> list is still worth calling — it records that this task was reviewed and
    /// nothing was flagged, not that no review happened (the review's own summary is recorded separately, on its
    /// <c>factory.step</c> row).</summary>
    Task SaveReviewFindingsAsync(Guid taskId, Guid runId, string agent, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken);

    /// <summary>Every finding recorded for a task across every review invocation, oldest first — the actionable,
    /// structured record SF-702 requires, rather than findings only ever visible as raw agent stdout.</summary>
    Task<IReadOnlyList<PersistedReviewFinding>> GetReviewFindingsAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>Records which agent is actually invoked for a task's current attempt, from the moment it is
    /// selected (before the process starts) until that invocation finishes (pass <see langword="null"/> to
    /// clear it) — the live signal of which provider is really running a task right now, distinct from
    /// <see cref="FactoryTask.PreferredAgent"/> (a preference, not necessarily who ends up invoked after a
    /// fallback) and from <c>factory.agent_run.agent</c> (only recorded after an invocation finishes).</summary>
    Task SetCurrentAgentAsync(Guid taskId, string? agentName, CancellationToken cancellationToken);

    /// <summary>Persists the provider session id this task's just-finished invocation reported, alongside the
    /// agent it belongs to (SF-701) — read back from <see cref="FactoryTask.ResumableSessionId"/>/<see cref="FactoryTask.ResumableSessionAgent"/>
    /// by the orchestrator's next invocation, only when that invocation picks the *same* agent, and ignored
    /// (never force-fed into a different provider's CLI) otherwise. Pass <see langword="null"/> for
    /// <paramref name="sessionId"/> when the invocation's agent does not support or report one, which clears any
    /// previously recorded session for <paramref name="agentName"/>.</summary>
    Task SetResumableSessionAsync(Guid taskId, string agentName, string? sessionId, CancellationToken cancellationToken);

    /// <summary>Whether new dispatch is currently paused factory-wide (<see cref="DispatchPauseScope.Global"/>).
    /// Checked once per poll iteration, before claiming — never mid-task, so a task already claimed and
    /// executing always finishes undisturbed.</summary>
    Task<bool> IsDispatchPausedAsync(CancellationToken cancellationToken);

    /// <summary>Whether this specific agent is currently paused, reserving its capacity for interactive use.
    /// Checked wherever an agent's availability is decided, alongside <see cref="IsAgentAtQuotaAsync"/>, so a
    /// paused provider is never treated as available for a new selection or for resuming waiting work.</summary>
    Task<bool> IsAgentPausedAsync(string agent, CancellationToken cancellationToken);

    /// <summary>The current pause state for one scope (<see cref="DispatchPauseScope.Global"/> or an agent's
    /// name), or <see cref="DispatchPauseState.NotPaused"/> if no row has ever been written for it.</summary>
    Task<DispatchPauseState> GetDispatchPauseAsync(string scope, CancellationToken cancellationToken);

    /// <summary>Every scope with a recorded pause row — global and/or per-agent — for the dashboard to show
    /// every current pause at once. A scope never paused and never explicitly resumed has no row and is
    /// therefore absent here, not merely reported as not paused.</summary>
    Task<IReadOnlyList<DispatchPauseState>> GetAllDispatchPausesAsync(CancellationToken cancellationToken);

    /// <summary>Pauses or resumes one scope (<see cref="DispatchPauseScope.Global"/> or an agent's name).
    /// Resuming (<paramref name="paused"/> <see langword="false"/>) clears <paramref name="reason"/> and the
    /// recorded actor/time along with it, so a later query never shows a stale reason for a pause that is no
    /// longer in effect.</summary>
    Task SetDispatchPauseAsync(string scope, bool paused, string? reason, string actor, CancellationToken cancellationToken);

    /// <summary>Sets a task's claim-ordering priority (SF-611): among every eligible task, <see cref="ClaimNextAsync"/>
    /// always claims the highest priority first (ties broken by creation order). Never restricted by status — a
    /// task not yet eligible to claim can still be reprioritized ahead of time.</summary>
    Task SetPriorityAsync(Guid taskId, int priority, CancellationToken cancellationToken);

    /// <summary>Records that <paramref name="taskId"/> must wait for <paramref name="dependsOnTaskId"/> to reach
    /// <see cref="FactoryTaskStatus.Completed"/> before it becomes claimable (SF-611). Rejects a self-dependency,
    /// a dependency on a nonexistent task, and any edge that would close a cycle with an existing dependency
    /// chain — checked transitively, not just the direct edge. Adding an edge that already exists is idempotent.</summary>
    Task<AddDependencyOutcome> AddDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken);

    /// <summary>Removes one dependency edge, if present — the operator's way to unblock a task whose prerequisite
    /// was wrong, already handled another way, or no longer applies. A no-op if the edge does not exist.</summary>
    Task RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken);

    /// <summary>Every prerequisite <paramref name="taskId"/> currently depends on, with each prerequisite's own
    /// title and status, so the operator can see exactly what is blocking a task without a second lookup.</summary>
    Task<IReadOnlyList<TaskDependency>> GetDependenciesAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>The factory task produced from a given repository's given GitHub issue number (SF-710), or
    /// <see langword="null"/> if that repository is unknown, the issue has not been synced yet, or the issue was
    /// never eligible to produce a task (e.g. missing <c>factory:ready</c>) — all three are indistinguishable to
    /// the caller and are retried on a later sync pass rather than treated as an error.</summary>
    Task<Guid?> FindTaskIdForIssueAsync(string owner, string name, int issueNumber, CancellationToken cancellationToken);

    /// <summary>Reconciles <paramref name="taskId"/>'s <c>source='issue'</c> dependency edges (SF-710) against
    /// <paramref name="parsedDependsOnTaskIds"/> — the current set parsed from its GitHub issue body on this sync
    /// pass. Adds a new edge for each id not already present (tagged <c>source='issue'</c>), removes an existing
    /// <c>source='issue'</c> edge whose id is no longer in the set, and never touches a manually-added (SF-611
    /// dashboard, <c>source IS NULL</c>) edge either way. A parsed id that would close a dependency cycle (reusing
    /// the same transitive check <see cref="AddDependencyAsync"/> uses) is skipped, not inserted, and reported in
    /// the result rather than silently dropped.</summary>
    Task<IssueDependencyReconciliation> ReconcileIssueDependenciesAsync(Guid taskId, IReadOnlyList<Guid> parsedDependsOnTaskIds, CancellationToken cancellationToken);

    /// <summary>Moves a <see cref="FactoryTaskStatus.Pending"/> task to <see cref="FactoryTaskStatus.NeedsHuman"/>
    /// the moment any of its prerequisites ends at <see cref="FactoryTaskStatus.Rejected"/>,
    /// <see cref="FactoryTaskStatus.Cancelled"/>, or <see cref="FactoryTaskStatus.Failed"/> — a prerequisite that
    /// will never merge must never silently strand its dependent in the queue forever, nor silently release it to
    /// run against a base that will never actually contain the prerequisite's changes; it requires an explicit
    /// operator decision (retry the prerequisite, remove the dependency, or cancel the dependent) instead. Called
    /// once per poll cycle; returns how many tasks were moved.</summary>
    Task<int> BlockDependentsOnFailedPrerequisitesAsync(CancellationToken cancellationToken);

    /// <summary>Counts tasks currently resting in <see cref="FactoryTaskStatus.ReadyForPublish"/> (validated,
    /// waiting to be pushed) or <see cref="FactoryTaskStatus.Published"/> (already pushed, waiting on human
    /// review/merge) — the operator's outstanding review backlog (SF-612), the same set <see cref="ClaimNextAsync"/>
    /// caps a new <see cref="FactoryTaskStatus.Pending"/> claim against. Counts current task status directly
    /// rather than <c>factory.publication</c> rows, so a task with more than one publication attempt (a retry, or
    /// a reclaimed stale lease) is never counted more than once.</summary>
    Task<int> CountOutstandingReviewWorkAsync(CancellationToken cancellationToken);

    /// <summary>Records one attempt to write to GitHub (a comment or a state-label change) as append-only operational
    /// state, regardless of whether it succeeded.</summary>
    Task RecordGitHubWriteAsync(Guid taskId, string kind, string detail, bool succeeded, string? error, CancellationToken cancellationToken);

    /// <summary>Tasks resting in <see cref="FactoryTaskStatus.Published"/> with a known pull request, for the
    /// GitHub sync worker to observe and resolve to <see cref="FactoryTaskStatus.Completed"/> or <see cref="FactoryTaskStatus.Rejected"/>.</summary>
    Task<IReadOnlyList<PublishedTaskRef>> GetPublishedTasksAsync(CancellationToken cancellationToken);

    /// <summary>Persists (upserts) the most recently synchronized CI status for a task's published pull
    /// request (SF-614) — always fully overwritten with what was just fetched, never merged with a previous
    /// poll's, so a stale status can never be presented alongside a newer head commit than the one it actually
    /// describes.</summary>
    Task SetCiStatusAsync(Guid taskId, string overallStatus, string? headCommit, IReadOnlyList<PullRequestCheck> checks, string? error, CancellationToken cancellationToken);

    /// <summary>The most recently synchronized CI status for a task, or <see langword="null"/> if it has never
    /// been synchronized (never published, or not yet polled).</summary>
    Task<TaskCiStatus?> GetCiStatusAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>Automatically continues a task whose published pull request's CI failed on its exact current head
    /// commit (SF-706), the same <see cref="FactoryTaskStatus.Published"/>→<see cref="FactoryTaskStatus.Pending"/>
    /// path <see cref="ContinueWithFeedbackAsync"/> uses for a human continuation, but attributed to the
    /// orchestrator rather than a human, and restricted to a task currently resting in exactly
    /// <see cref="FactoryTaskStatus.Published"/>. Also records <paramref name="headCommit"/>, in the same
    /// transaction, as the commit this repair was triggered for (surfaced back via <see cref="TaskCiStatus.RepairTriggeredForCommit"/>)
    /// so the caller never triggers a second repair for the same still-failing commit. Returns <see langword="false"/>
    /// if the task is no longer resting in <see cref="FactoryTaskStatus.Published"/> (already resolved by a
    /// concurrent action).</summary>
    Task<bool> TriggerCiRepairAsync(Guid taskId, string headCommit, string feedback, CancellationToken cancellationToken);

    /// <summary>Small outcome/review-effort metrics computed directly from <c>task_event</c>, <c>agent_run</c>,
    /// <c>task_ci_status</c>, and any recorded review time, since <paramref name="since"/> (SF-617). See
    /// <see cref="OutcomeMetrics"/> for exact per-field definitions.</summary>
    Task<OutcomeMetrics> GetOutcomeMetricsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Records how many minutes a human spent reviewing a task — optional, operator-entered, never
    /// inferred (SF-617). Overwrites any previously recorded value and its timestamp.</summary>
    Task SetReviewMinutesAsync(Guid taskId, int minutes, CancellationToken cancellationToken);

    /// <summary>How many invocations count toward this task's <see cref="RepositoryConfiguration.MaxImplementationAttempts"/>
    /// budget — a quota-interrupted invocation (see <see cref="AgentRunRecord.CountsAsImplementationAttempt"/>)
    /// never got a real chance to implement anything, so it is excluded here even though it is still recorded in
    /// full. Only counts runs since the task's most recent <see cref="ContinueWithFeedbackAsync"/> call, if any
    /// (SF-613) — an explicit human continuation always grants a bounded, fresh budget rather than either
    /// staying permanently exhausted or granting unlimited retries; with no recorded feedback, this counts
    /// every run across the task's whole history, exactly as before SF-613.</summary>
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
    /// both not currently at quota (per <see cref="RecordAgentQuotaStatusAsync"/>) and not operator-paused (per
    /// <see cref="SetDispatchPauseAsync"/>) — scheduled from provider availability, never a waiting task's own
    /// invocation history, so a task that was never actually invoked (every provider was already at quota on its
    /// first attempt) is never stuck forever, and never resumed onto a provider the operator deliberately
    /// reserved for interactive use. Resumes at most one task per call, deliberately: see the implementation for
    /// why. Returns how many were resumed (0 or 1).</summary>
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

    /// <summary>Operator override for a stale quota-detected status: clears <see cref="AgentQuotaStatus.Detected"/>
    /// and <see cref="AgentQuotaStatus.ResetAt"/> for this agent so <see cref="IsAgentAtQuotaAsync"/> returns
    /// <see langword="false"/> immediately, without waiting for the persisted (possibly wrong) reset time to pass
    /// or for another failed invocation to overwrite it. Exists because a detected reset time can be a bounded
    /// <see cref="QuotaResetKind.Estimated"/> guess that outlives the provider's real, shorter reset — previously
    /// the only way to recover was editing <c>factory.agent_availability</c> directly. Returns <see
    /// langword="false"/> if no quota status has ever been recorded for this agent (nothing to clear).</summary>
    Task<bool> ClearAgentQuotaAsync(string agent, CancellationToken cancellationToken);

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
    /// then. Pass a value derived from the time the sync cycle started, not when it finished, so an issue updated
    /// while this cycle was still running is safely re-fetched next time rather than silently skipped. Callers
    /// should backdate that start time by <see cref="SyncCheckpoint.SafetyMargin"/> (see <see cref="SyncCheckpoint"/>)
    /// so a brief GitHub search-indexing lag cannot permanently skip an issue either.</summary>
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

    /// <summary>CI check status for a pull request's current head commit (SF-614). Never throws or returns
    /// <see langword="null"/> on a read failure — reported explicitly via <see cref="PullRequestChecksResult.Error"/>
    /// instead, so an authentication or network failure is never silently indistinguishable from "no checks configured."</summary>
    Task<PullRequestChecksResult> GetPullRequestChecksAsync(string owner, string name, int number, CancellationToken cancellationToken);
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

/// <summary>Reads a review invocation's <c>.factory/review.json</c> (SF-702), mirroring <see cref="IAgentResultReader"/>.</summary>
public interface IAgentReviewResultReader { Task<(AgentReviewResult? Result, string? Error)> ReadAsync(string worktreePath, CancellationToken cancellationToken); }

/// <summary>
/// The orchestrator's only path to writing to GitHub. Never pushes to anything but the task's own factory branch.
/// Merging (SF-709) is the one exception to "never merges": <c>Factory.GitHubSync.Worker</c> — never
/// <c>PublicationExecutor</c>, which still only ever pushes and opens a pull request — may call
/// <see cref="MergePullRequestAsync"/> for a task whose own <see cref="PublishedTaskRef.RequireHumanMerge"/> is
/// <see langword="false"/>, and only after independently observing CI green for that pull request's exact head
/// commit. A task marked <c>HUMAN REVIEW</c> is never merged this way; merging it stays an exclusively human
/// action performed on GitHub itself, exactly as every task's merge worked before SF-709.
/// </summary>
public interface IGitHubPublisher
{
    Task<PushResult> PushAsync(string worktreePath, string branchName, CancellationToken cancellationToken);

    /// <summary>The existing open pull request for this branch, if any — checked before creating a new one so a
    /// retried publication (for example after a crash right after a prior attempt's <c>gh pr create</c> already
    /// succeeded) never creates a duplicate. <see langword="null"/> means none was found; a returned
    /// <see cref="PullRequestResult"/> with <c>Succeeded=false</c> means the check itself failed.</summary>
    Task<PullRequestResult?> FindExistingPullRequestAsync(string owner, string name, string branchName, CancellationToken cancellationToken);

    /// <param name="draft">Whether to open the pull request as a draft (SF-709): <see langword="true"/> for a task
    /// requiring human merge, exactly as every pull request was opened before SF-709; <see langword="false"/> opens
    /// it ready for review immediately, since no human is expected to look at it before an automatic merge.</param>
    Task<PullRequestResult> CreatePullRequestAsync(string owner, string name, string branchName, string baseBranch, string title, string body, bool draft, CancellationToken cancellationToken);

    /// <summary>Posts a comment on the issue backing a task, so people who work in GitHub see what the factory did
    /// without opening the dashboard.</summary>
    Task<GitHubWriteResult> CommentOnIssueAsync(string owner, string name, int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>Sets the one <c>factory:*</c> state label that reflects a task's current outcome, removing whichever
    /// other state label the issue previously carried.</summary>
    Task<GitHubWriteResult> SetStateLabelAsync(string owner, string name, int issueNumber, string label, CancellationToken cancellationToken);

    /// <summary>Requests GitHub merge this pull request right now (SF-709) — never <c>--auto</c>, since the caller
    /// has already independently confirmed CI is green for its exact head commit, not merely enqueued a merge for
    /// whenever checks eventually pass. A conflict, a protected-branch rejection, or an authentication failure
    /// comes back as <c>Succeeded=false</c> with the real <c>gh</c> error text, never thrown.</summary>
    Task<MergeResult> MergePullRequestAsync(string owner, string name, int number, CancellationToken cancellationToken);
}
