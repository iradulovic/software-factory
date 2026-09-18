using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Writes <c>.factory/task.md</c> for the agent and resolves the validation configuration the run will use.
/// The configuration is read from the fetched base branch reference, not the worktree, and persisted on the
/// run immediately, before the agent ever executes.
/// </summary>
public sealed class WriteContextStep(ITaskStore tasks, ITaskContextWriter contextWriter, IRepositoryConfigurationReader configurationReader) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "WriteContext", 1, cancellationToken);
        var worktree = context.Worktree!;
        await contextWriter.WriteAsync(worktree.Path, context.Repository!, context.Issue, context.Task, cancellationToken);

        var configuration = await configurationReader.ReadAsync(worktree.Path, context.BaseRef, cancellationToken);
        context.Configuration = configuration;
        await tasks.SetRunConfigurationAsync(context.RunId, configuration, cancellationToken);

        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, worktree.Path, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
