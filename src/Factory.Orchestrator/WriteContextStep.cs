using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Writes <c>.factory/task.md</c> for the agent and resolves the validation configuration the run will use.
/// The configuration is read from the fetched base branch reference, not the worktree, and persisted on the
/// run immediately, before the agent ever executes. Also enforces the configured implementation-attempt budget:
/// once exhausted, the task fails here, before ever invoking the agent again, with a reason explicit enough that
/// a human knows this is a terminal outcome rather than one more transient failure to retry.
/// </summary>
public sealed class WriteContextStep(ITaskStore tasks, ITaskContextWriter contextWriter, IRepositoryConfigurationReader configurationReader) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "WriteContext", 1, cancellationToken);
        var worktree = context.Worktree!;

        var configuration = await configurationReader.ReadAsync(worktree.Path, context.BaseRef, cancellationToken);
        context.Configuration = configuration;
        await tasks.SetRunConfigurationAsync(context.RunId, configuration, cancellationToken);

        var attemptNumber = await tasks.CountAgentRunsAsync(context.Task.Id, cancellationToken) + 1;
        if (attemptNumber > configuration.MaxImplementationAttempts)
        {
            var limitReason = $"Implementation attempt limit ({configuration.MaxImplementationAttempts}) reached; " +
                "this task will not be retried automatically. A human must change the task, the repository, or the limit before retrying.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, limitReason, null, cancellationToken);
            return PipelineStepResult.Failed(limitReason);
        }
        context.AttemptNumber = attemptNumber;

        var previous = attemptNumber > 1 ? await tasks.GetPreviousAttemptAsync(context.Task.Id, cancellationToken) : null;
        // The most recent operator feedback, if any (SF-613) — surfaced on every attempt within its cycle, not
        // just the first, since a repeat automatic repair (SF-606) still needs to address it too.
        var feedback = (await tasks.GetFeedbackAsync(context.Task.Id, cancellationToken)).LastOrDefault();
        context.AgentPurpose = string.Equals(feedback?.CreatedBy, MergeConflictRepair.CreatedBy, StringComparison.OrdinalIgnoreCase)
            ? AgentRunPurpose.MergeConflict
            : AgentRunPurpose.Implement;
        await contextWriter.WriteAsync(worktree.Path, context.Repository!, context.Issue, context.Task,
            new AttemptContext(attemptNumber, configuration.MaxImplementationAttempts, previous, feedback), cancellationToken);

        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, worktree.Path, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
