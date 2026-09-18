using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Confirms the agent actually changed the worktree. A "completed" result with no diff is a failure, not a
/// pass-through to validation, since validating an unchanged base branch would misreport the task as done.
/// </summary>
public sealed class CollectDiffStep(ITaskStore tasks, IWorktreeInspector inspector) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "CollectDiff", 1, cancellationToken);
        var hasChanges = await inspector.HasChangesAsync(context.Worktree!.Path, context.BaseRef, cancellationToken);
        if (!hasChanges)
        {
            const string reason = "Agent reported completion but the worktree contains no changes.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }

        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, null, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
