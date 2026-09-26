using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// The result of one pipeline step. <see cref="PipelineOutcome.Succeeded"/> lets the executor continue to
/// the next step; every other outcome ends the run with the matching task status.
/// </summary>
public enum PipelineOutcome { Succeeded, Failed, NeedsHuman, WaitingForQuota }

/// <param name="Repairable">Only meaningful with <see cref="PipelineOutcome.Failed"/> (SF-606): whether the
/// executor may automatically reschedule another implementation attempt for this failure, subject to the
/// repository's own implementation-attempt budget, instead of ending the task at <c>Failed</c> and waiting for a
/// human. Defaults to <see langword="false"/> — a step opts in explicitly; nothing is auto-repaired by accident.</param>
public sealed record PipelineStepResult(PipelineOutcome Outcome, string? Reason = null, bool Repairable = false)
{
    public static readonly PipelineStepResult Ok = new(PipelineOutcome.Succeeded);
    public static PipelineStepResult Failed(string reason, bool repairable = false) => new(PipelineOutcome.Failed, reason, repairable);
    public static PipelineStepResult NeedsHuman(string reason) => new(PipelineOutcome.NeedsHuman, reason);
    public static PipelineStepResult WaitingForQuota(string reason) => new(PipelineOutcome.WaitingForQuota, reason);
}

/// <summary>
/// Mutable state threaded through the pipeline. Each step reads what earlier steps produced and may add its own;
/// nothing here is persisted directly; steps persist through <see cref="ITaskStore"/> as they go.
/// </summary>
public sealed class PipelineContext(FactoryTask task, Guid runId)
{
    public FactoryTask Task { get; } = task;
    public Guid RunId { get; } = runId;

    /// <summary>The task's status as last recorded by the executor, so a failure can transition from wherever execution actually is.</summary>
    public FactoryTaskStatus CurrentStatus { get; set; } = task.Status;

    public GitHubRepository? Repository { get; set; }
    public GitHubIssue? Issue { get; set; }
    public WorktreeLocation? Worktree { get; set; }
    public RepositoryConfiguration? Configuration { get; set; }
    public AgentResult? AgentResult { get; set; }
    public string? ImplementingAgent { get; set; }
    public string? ImplementationSessionId { get; set; }
    public int ReviewFixAttempts { get; set; }
    public ChangeSummary? ChangeSummary { get; set; }

    /// <summary>Whether this task opted into a second-agent review pass (SF-702), decided once by
    /// <see cref="PreparePublicationStep"/> and consumed by the executor to decide whether to run
    /// <see cref="ReviewStep"/> at all.</summary>
    public bool ReviewRequested { get; set; }

    /// <summary>Which implementation attempt this run represents, set by <see cref="WriteContextStep"/> once the
    /// prior attempt count is known.</summary>
    public int AttemptNumber { get; set; } = 1;

    /// <summary>The task's base branch as a fetched remote-tracking reference, e.g. <c>origin/main</c>.</summary>
    public string BaseRef => $"origin/{Task.BaseBranch}";
}

/// <summary>
/// One stage of task execution. A step owns its own <c>factory.step</c> row(s) and must complete every step it
/// starts before returning a result; the executor interprets only the returned outcome, never step internals.
/// A step must not catch and swallow unexpected exceptions: letting them propagate is what lets the executor's
/// top-level handler close an orphaned running step and fail the task cleanly.
/// </summary>
public interface IPipelineStep
{
    Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken);
}
