using Factory.Core;

namespace Factory.Orchestrator.Tests;

/// <summary>In-memory <see cref="ITaskStore"/> that records every transition, step, run, and closure.</summary>
internal sealed class FakeTaskStore : ITaskStore
{
    public sealed record StepRecord(Guid RunId, string StepType, ExecutionStatus Status, string? Error, string? Output);

    public FactoryTaskStatus Status { get; set; } = FactoryTaskStatus.Claimed;
    public List<(FactoryTaskStatus From, FactoryTaskStatus To, string? Reason)> Transitions { get; } = [];
    public Dictionary<Guid, StepRecord> Steps { get; } = [];
    public List<string> StepOrder { get; } = [];
    public Dictionary<Guid, ExecutionStatus> Runs { get; } = [];
    public Dictionary<Guid, RepositoryConfiguration> RunConfigurations { get; } = [];
    public Dictionary<Guid, ChangeSummary> ChangeSummaries { get; } = [];
    public List<AgentRunRecord> AgentRuns { get; } = [];
    public List<(Guid TaskId, Guid? RunId, string RequestedBy)> PublicationRequests { get; } = [];
    public bool NextPublicationRequestAllowed { get; set; } = true;
    public List<(Guid Id, string Status, int? PullRequestNumber, string? PullRequestUrl, string? Error)> CompletedPublications { get; } = [];
    public int ReconciledPublishedTasks { get; set; }
    public List<(Guid TaskId, string HeadCommit)> ValidatedHeadCommits { get; } = [];
    public List<(Guid TaskId, string Kind, string Detail, bool Succeeded, string? Error)> GitHubWrites { get; } = [];
    public List<PublishedTaskRef> PublishedTasks { get; } = [];
    public PreviousAttemptSummary? PreviousAttempt { get; set; }
    public int ExpiredQuotaTasksToResume { get; set; }
    public (Guid RunId, ExecutionStatus Status, string Reason)? Closed { get; private set; }
    public (string Branch, string Path)? Workspace { get; private set; }
    public bool LeaseReleased { get; private set; }
    public Func<CancellationToken, Task<bool>> RenewLease { get; init; } = _ => Task.FromResult(true);

    public Task<FactoryTask?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken) => Task.FromResult<FactoryTask?>(null);
    public Task<bool> RenewLeaseAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken cancellationToken) => RenewLease(cancellationToken);
    public Task ReleaseLeaseAsync(Guid taskId, string workerId, CancellationToken cancellationToken) { LeaseReleased = true; return Task.CompletedTask; }
    public Task<bool> CreateForIssueIfEligibleAsync(GitHubIssue issue, string baseBranch, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RetryAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> CancelAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(false);
    public List<string> FeedbackRecorded { get; } = [];
    public bool NextContinueWithFeedbackAllowed { get; set; } = true;
    public Task<bool> ContinueWithFeedbackAsync(Guid taskId, string feedback, CancellationToken cancellationToken)
    {
        if (NextContinueWithFeedbackAllowed) FeedbackRecorded.Add(feedback);
        return Task.FromResult(NextContinueWithFeedbackAllowed);
    }
    public IReadOnlyList<TaskFeedback> Feedback { get; set; } = [];
    public Task<IReadOnlyList<TaskFeedback>> GetFeedbackAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(Feedback);
    public Task<bool> CancelPendingForIssueAsync(long issueId, string reason, CancellationToken cancellationToken) => Task.FromResult(false);

    public List<(string WorkerId, string Host, Guid? CurrentTaskId)> Heartbeats { get; } = [];
    public Task RecordHeartbeatAsync(string workerId, string host, Guid? currentTaskId, CancellationToken cancellationToken)
    {
        Heartbeats.Add((workerId, host, currentTaskId));
        return Task.CompletedTask;
    }

    public List<WorktreeCleanupCandidate> WorktreeCleanupCandidates { get; } = [];
    public Task<IReadOnlyList<WorktreeCleanupCandidate>> GetWorktreeCleanupCandidatesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorktreeCleanupCandidate>>(WorktreeCleanupCandidates);

    public List<(Guid TaskId, FactoryTaskStatus ExpectedStatus)> ClearedWorkspaces { get; } = [];
    public bool NextClearWorkspaceSucceeds { get; set; } = true;
    public Task<bool> ClearWorkspaceIfStatusUnchangedAsync(Guid taskId, FactoryTaskStatus expectedStatus, CancellationToken cancellationToken)
    {
        ClearedWorkspaces.Add((taskId, expectedStatus));
        return Task.FromResult(NextClearWorkspaceSucceeds);
    }

    public Task TransitionAsync(Guid taskId, FactoryTaskStatus expected, FactoryTaskStatus next, string? failureReason, CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(expected, next);
        if (Status != expected) throw new InvalidOperationException($"Task {taskId} was not in expected state {expected}.");
        Status = next;
        Transitions.Add((expected, next, failureReason));
        return Task.CompletedTask;
    }

    public Task SetWorkspaceAsync(Guid taskId, string branchName, string worktreePath, CancellationToken cancellationToken) { Workspace = (branchName, worktreePath); return Task.CompletedTask; }

    public Task<Guid> StartRunAsync(Guid taskId, string workerId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid(); Runs[id] = ExecutionStatus.Running; return Task.FromResult(id);
    }

    public Task<Guid> StartStepAsync(Guid runId, string stepType, int attempt, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid(); Steps[id] = new StepRecord(runId, stepType, ExecutionStatus.Running, null, null); StepOrder.Add(stepType); return Task.FromResult(id);
    }

    public Task CompleteStepAsync(Guid stepId, ExecutionStatus status, string? error, string? output, CancellationToken cancellationToken)
    {
        Steps[stepId] = Steps[stepId] with { Status = status, Error = error, Output = output }; return Task.CompletedTask;
    }

    public Task SaveAgentRunAsync(AgentRunRecord run, CancellationToken cancellationToken) { AgentRuns.Add(run); return Task.CompletedTask; }
    public Task CompleteRunAsync(Guid runId, ExecutionStatus status, CancellationToken cancellationToken) { Runs[runId] = status; return Task.CompletedTask; }
    public Task SetRunConfigurationAsync(Guid runId, RepositoryConfiguration configuration, CancellationToken cancellationToken) { RunConfigurations[runId] = configuration; return Task.CompletedTask; }

    public Task CloseExecutionAsync(Guid runId, ExecutionStatus status, string reason, CancellationToken cancellationToken)
    {
        foreach (var (id, step) in Steps.Where(s => s.Value.RunId == runId && s.Value.Status == ExecutionStatus.Running).ToList())
            Steps[id] = step with { Status = status, Error = reason };
        if (Runs.TryGetValue(runId, out var current) && current == ExecutionStatus.Running) Runs[runId] = status;
        Closed = (runId, status, reason);
        return Task.CompletedTask;
    }

    public Task SetChangeSummaryAsync(Guid runId, ChangeSummary summary, CancellationToken cancellationToken) { ChangeSummaries[runId] = summary; return Task.CompletedTask; }

    public Task<Guid?> RequestPublicationAsync(Guid taskId, Guid? runId, string requestedBy, CancellationToken cancellationToken)
    {
        PublicationRequests.Add((taskId, runId, requestedBy));
        return Task.FromResult(NextPublicationRequestAllowed ? Guid.NewGuid() : (Guid?)null);
    }

    public Task<PublicationRequest?> ClaimNextPublicationAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken) => Task.FromResult<PublicationRequest?>(null);

    public Task CompletePublicationAsync(Guid publicationId, string status, int? pullRequestNumber, string? pullRequestUrl, string? error, CancellationToken cancellationToken)
    {
        CompletedPublications.Add((publicationId, status, pullRequestNumber, pullRequestUrl, error));
        return Task.CompletedTask;
    }

    public Task<int> ReconcilePublishedTasksAsync(CancellationToken cancellationToken) => Task.FromResult(ReconciledPublishedTasks);

    public Task SetValidatedHeadCommitAsync(Guid taskId, string headCommit, CancellationToken cancellationToken)
    {
        ValidatedHeadCommits.Add((taskId, headCommit));
        return Task.CompletedTask;
    }

    public List<string?> CurrentAgentCalls { get; } = [];
    public Task SetCurrentAgentAsync(Guid taskId, string? agentName, CancellationToken cancellationToken)
    {
        CurrentAgentCalls.Add(agentName);
        return Task.CompletedTask;
    }

    public List<(Guid TaskId, int Priority)> PrioritiesSet { get; } = [];
    public Task SetPriorityAsync(Guid taskId, int priority, CancellationToken cancellationToken)
    {
        PrioritiesSet.Add((taskId, priority));
        return Task.CompletedTask;
    }

    public List<(Guid TaskId, Guid DependsOnTaskId)> DependenciesAdded { get; } = [];
    public AddDependencyOutcome NextAddDependencyOutcome { get; set; } = AddDependencyOutcome.Added;
    public Task<AddDependencyOutcome> AddDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken)
    {
        DependenciesAdded.Add((taskId, dependsOnTaskId));
        return Task.FromResult(NextAddDependencyOutcome);
    }

    public List<(Guid TaskId, Guid DependsOnTaskId)> DependenciesRemoved { get; } = [];
    public Task RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken)
    {
        DependenciesRemoved.Add((taskId, dependsOnTaskId));
        return Task.CompletedTask;
    }

    public List<TaskDependency> Dependencies { get; set; } = [];
    public Task<IReadOnlyList<TaskDependency>> GetDependenciesAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TaskDependency>>(Dependencies.Where(d => d.TaskId == taskId).ToList());

    public int TasksToBlockOnFailedPrerequisites { get; set; }
    public Task<int> BlockDependentsOnFailedPrerequisitesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(TasksToBlockOnFailedPrerequisites);

    public int OutstandingReviewWorkCount { get; set; }
    public Task<int> CountOutstandingReviewWorkAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OutstandingReviewWorkCount);

    public Task RecordGitHubWriteAsync(Guid taskId, string kind, string detail, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        GitHubWrites.Add((taskId, kind, detail, succeeded, error));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PublishedTaskRef>> GetPublishedTasksAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PublishedTaskRef>>(PublishedTasks);

    public List<(Guid TaskId, string OverallStatus, string? HeadCommit, IReadOnlyList<PullRequestCheck> Checks, string? Error)> CiStatusesSet { get; } = [];
    public Task SetCiStatusAsync(Guid taskId, string overallStatus, string? headCommit, IReadOnlyList<PullRequestCheck> checks, string? error, CancellationToken cancellationToken)
    {
        CiStatusesSet.Add((taskId, overallStatus, headCommit, checks, error));
        return Task.CompletedTask;
    }
    public TaskCiStatus? CiStatus { get; set; }
    public Task<TaskCiStatus?> GetCiStatusAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(CiStatus);

    public OutcomeMetrics? OutcomeMetrics { get; set; }
    public Task<OutcomeMetrics> GetOutcomeMetricsAsync(DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult(OutcomeMetrics!);
    public List<(Guid TaskId, int Minutes)> ReviewMinutesSet { get; } = [];
    public Task SetReviewMinutesAsync(Guid taskId, int minutes, CancellationToken cancellationToken) { ReviewMinutesSet.Add((taskId, minutes)); return Task.CompletedTask; }

    public Task<int> CountAgentRunsAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(AgentRuns.Count(r => r.TaskId == taskId && r.CountsAsImplementationAttempt));
    public Task<int> CountQuotaInterruptionsAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(AgentRuns.Count(r => r.TaskId == taskId && !r.CountsAsImplementationAttempt));
    public Task<PreviousAttemptSummary?> GetPreviousAttemptAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(PreviousAttempt);
    public List<IReadOnlyList<string>> ResumeExpiredQuotaTasksCalls { get; } = [];
    public Task<int> ResumeExpiredQuotaTasksAsync(IReadOnlyList<string> configuredAgents, CancellationToken cancellationToken)
    {
        ResumeExpiredQuotaTasksCalls.Add(configuredAgents);
        return Task.FromResult(ExpiredQuotaTasksToResume);
    }

    public HashSet<string> AgentsAtQuota { get; } = [];
    public Task<bool> IsAgentAtQuotaAsync(string agent, CancellationToken cancellationToken) => Task.FromResult(AgentsAtQuota.Contains(agent));

    public List<AgentQuotaStatus> RecordedQuotaStatuses { get; } = [];
    public Task RecordAgentQuotaStatusAsync(AgentQuotaStatus status, CancellationToken cancellationToken) { RecordedQuotaStatuses.Add(status); return Task.CompletedTask; }
    public Task<AgentQuotaStatus?> GetAgentQuotaStatusAsync(string agent, CancellationToken cancellationToken) =>
        Task.FromResult(RecordedQuotaStatuses.LastOrDefault(s => s.Agent == agent));

    public List<string> ClearedQuotaAgents { get; } = [];
    public Task<bool> ClearAgentQuotaAsync(string agent, CancellationToken cancellationToken)
    {
        var removedFromQuota = AgentsAtQuota.Remove(agent);
        var lastStatus = RecordedQuotaStatuses.LastOrDefault(s => s.Agent == agent);
        if (!removedFromQuota && lastStatus is null) return Task.FromResult(false);

        ClearedQuotaAgents.Add(agent);
        if (lastStatus is not null)
            RecordedQuotaStatuses.Add(lastStatus with { Detected = false, ResetAt = null, ResetKind = QuotaResetKind.None, Window = QuotaWindow.None });
        return Task.FromResult(true);
    }

    public bool DispatchPaused { get; set; }
    public HashSet<string> PausedAgents { get; } = [];
    public Dictionary<string, DispatchPauseState> DispatchPauses { get; } = [];
    public List<(string Scope, bool Paused, string? Reason, string Actor)> DispatchPauseChanges { get; } = [];

    public Task<bool> IsDispatchPausedAsync(CancellationToken cancellationToken) => Task.FromResult(DispatchPaused);
    public Task<bool> IsAgentPausedAsync(string agent, CancellationToken cancellationToken) => Task.FromResult(PausedAgents.Contains(agent));

    public Task<DispatchPauseState> GetDispatchPauseAsync(string scope, CancellationToken cancellationToken) =>
        Task.FromResult(DispatchPauses.TryGetValue(scope, out var state) ? state : DispatchPauseState.NotPaused(scope));

    public Task<IReadOnlyList<DispatchPauseState>> GetAllDispatchPausesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DispatchPauseState>>(DispatchPauses.Values.ToList());

    public Task SetDispatchPauseAsync(string scope, bool paused, string? reason, string actor, CancellationToken cancellationToken)
    {
        DispatchPauseChanges.Add((scope, paused, reason, actor));
        DispatchPauses[scope] = paused
            ? new DispatchPauseState(scope, true, reason, DateTimeOffset.UtcNow, actor)
            : DispatchPauseState.NotPaused(scope);
        return Task.CompletedTask;
    }

    public StepRecord Step(string stepType) => Steps.Values.Single(s => s.StepType == stepType);
}
