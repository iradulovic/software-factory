using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>Creates (or reuses, during recovery) the task's isolated Git worktree and records its location.</summary>
public sealed class CreateWorktreeStep(ITaskStore tasks, IWorktreeManager worktrees) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "CreateWorktree", 1, cancellationToken);
        WorktreeLocation location;
        try
        {
            location = await worktrees.CreateAsync(context.Repository!, context.Task, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (context.Task.ReleaseId is not null)
        {
            var reason = $"The task worktree could not be created from captured release base branch '{context.Task.BaseBranch}'. Restore the branch or repository access before retrying; the task will not fall back to the repository default. {exception.Message}";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.NeedsHuman(reason);
        }
        context.Worktree = location;
        await tasks.SetWorkspaceAsync(context.Task.Id, location.BranchName, location.Path, cancellationToken);
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, location.Path, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
