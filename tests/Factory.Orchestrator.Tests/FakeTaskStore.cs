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

    public Task<PublicationRequest?> ClaimNextPublicationAsync(string workerId, CancellationToken cancellationToken) => Task.FromResult<PublicationRequest?>(null);

    public Task CompletePublicationAsync(Guid publicationId, string status, int? pullRequestNumber, string? pullRequestUrl, string? error, CancellationToken cancellationToken)
    {
        CompletedPublications.Add((publicationId, status, pullRequestNumber, pullRequestUrl, error));
        return Task.CompletedTask;
    }

    public StepRecord Step(string stepType) => Steps.Values.Single(s => s.StepType == stepType);
}
