using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Computes the orchestrator's own, independently verified account of what a task changed and confirms the
/// worktree is actually publishable: every change committed, and the branch the worktree started on. A task
/// cannot reach <see cref="FactoryTaskStatus.ReadyForPublish"/> without this succeeding, because "ready to
/// publish" has to mean a human (or a later automated push) can act on a clean, known branch and a real diff.
/// </summary>
public sealed class PreparePublicationStep(ITaskStore tasks, IWorktreeInspector inspector) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "PreparePublication", 1, cancellationToken);
        var summary = await inspector.SummarizeAsync(context.Worktree!.Path, context.BaseRef, cancellationToken);

        if (!summary.IsClean)
        {
            const string reason = "Worktree has uncommitted changes; the agent must commit its work before it can be published.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }
        if (!string.Equals(summary.CurrentBranch, context.Worktree.BranchName, StringComparison.Ordinal))
        {
            var reason = $"Worktree is on unexpected branch '{summary.CurrentBranch}' (expected '{context.Worktree.BranchName}').";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }

        await tasks.SetChangeSummaryAsync(context.RunId, summary, cancellationToken);
        var output = $"{summary.FilesChanged.Count} file(s) changed, +{summary.LinesAdded} -{summary.LinesRemoved}";
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, output, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
