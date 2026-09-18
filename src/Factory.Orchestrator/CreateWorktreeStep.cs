using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>Creates (or reuses, during recovery) the task's isolated Git worktree and records its location.</summary>
public sealed class CreateWorktreeStep(ITaskStore tasks, IWorktreeManager worktrees) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "CreateWorktree", 1, cancellationToken);
        var location = await worktrees.CreateAsync(context.Repository!, context.Task, cancellationToken);
        context.Worktree = location;
        await tasks.SetWorkspaceAsync(context.Task.Id, location.BranchName, location.Path, cancellationToken);
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, location.Path, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
