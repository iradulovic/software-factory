using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Confirms the agent actually changed the worktree, and committed those changes, before spending a full
/// build/test validation cycle on it. A "completed" result with no diff at all is a failure, not a pass-through
/// to validation, since validating an unchanged base branch would misreport the task as done; likewise, changes
/// left uncommitted are rejected here — immediately, with an actionable reason — rather than only being
/// discovered by <see cref="PreparePublicationStep"/> after validation has already run (SF-605).
/// </summary>
public sealed class CollectDiffStep(ITaskStore tasks, IWorktreeInspector inspector) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "CollectDiff", 1, cancellationToken);
        var summary = await inspector.SummarizeAsync(context.Worktree!.Path, context.BaseRef, cancellationToken);

        if (!summary.IsClean)
        {
            const string reason = "Agent made changes but did not commit them; committed work on this branch is required before it can be validated or published.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }
        if (summary.FilesChanged.Count == 0)
        {
            const string reason = "Agent reported completion but the worktree contains no changes.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }

        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, null, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
