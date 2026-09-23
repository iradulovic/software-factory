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
        // Recorded on the task itself, not just the run, so publication — which may happen long after this run
        // closed, and possibly be retried after a crash — can independently verify the worktree it is about to
        // push still matches exactly what was validated here.
        await tasks.SetValidatedHeadCommitAsync(context.Task.Id, summary.HeadCommit, cancellationToken);
        // SF-709: this task's effective merge policy, decided once, here, and never re-derived afterward — the
        // repository's own default, overridden to true (require a human) the moment its issue carries a HUMAN
        // REVIEW marker. Persisted on the task itself so both publication (draft vs. ready-for-review) and the
        // later GitHub-sync auto-merge decision read the exact same already-decided value.
        var requireHumanMerge = context.Configuration!.RequireHumanMerge || HumanReviewMarker.IsPresent(context.Issue);
        await tasks.SetRequireHumanMergeAsync(context.Task.Id, requireHumanMerge, cancellationToken);

        // SF-702: opt-in, never the default for every task — either the issue explicitly asked for it, or the
        // implementation agent's own result flagged at least one risk worth a second look. Disabled entirely
        // (regardless of either signal) when the repository sets MaxReviewAttempts to 0.
        var reviewRequested = context.Configuration.MaxReviewAttempts > 0 &&
            (ReviewRequestedMarker.IsPresent(context.Issue) || context.AgentResult?.Risks.Count > 0);
        await tasks.SetReviewRequestedAsync(context.Task.Id, reviewRequested, cancellationToken);
        context.ReviewRequested = reviewRequested;

        context.ChangeSummary = summary;
        var output = $"{summary.FilesChanged.Count} file(s) changed, +{summary.LinesAdded} -{summary.LinesRemoved}";
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, output, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
