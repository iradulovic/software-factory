using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Runs one claimed task through an ordered pipeline: preparation, worktree and context setup, agent
/// implementation, diff collection, and independent validation. This class owns only status transitions and
/// run/step-of-record bookkeeping; each stage's own logic lives in its <see cref="IPipelineStep"/>, which makes
/// every stage independently unit-testable with fakes for the boundary interfaces it depends on.
/// </summary>
public sealed class TaskExecutor(
    ITaskStore tasks,
    PrepareRepositoryStep prepareRepository,
    CreateWorktreeStep createWorktree,
    WriteContextStep writeContext,
    RunAgentStep runAgent,
    CollectDiffStep collectDiff,
    ValidateStep validate,
    PreparePublicationStep preparePublication,
    ILogger<TaskExecutor> logger)
{
    public async Task ExecuteAsync(FactoryTask task, Guid runId, CancellationToken cancellationToken)
    {
        var context = new PipelineContext(task, runId);
        try
        {
            await TransitionAsync(context, FactoryTaskStatus.Preparing, null, cancellationToken);
            if (!await RunStepAsync(prepareRepository, context, cancellationToken)) return;
            if (!await RunStepAsync(createWorktree, context, cancellationToken)) return;
            if (!await RunStepAsync(writeContext, context, cancellationToken)) return;

            await TransitionAsync(context, FactoryTaskStatus.Implementing, null, cancellationToken);
            if (!await RunStepAsync(runAgent, context, cancellationToken)) return;
            if (!await RunStepAsync(collectDiff, context, cancellationToken)) return;

            await TransitionAsync(context, FactoryTaskStatus.Validating, null, cancellationToken);
            if (!await RunStepAsync(validate, context, cancellationToken)) return;
            if (!await RunStepAsync(preparePublication, context, cancellationToken)) return;

            // ReadyForPublish is a resting state: a validated implementation waits here for a human (or, for an
            // auto-draft repository, the orchestrator's own request below) to actually publish it. The orchestrator
            // never assigns Completed on its own; only a successful publication does that.
            await TransitionAsync(context, FactoryTaskStatus.ReadyForPublish, null, cancellationToken);
            await tasks.CompleteRunAsync(runId, ExecutionStatus.Succeeded, cancellationToken);
            logger.LogInformation("Task {TaskId} is ready for publish in run {RunId}", task.Id, runId);

            if (context.Configuration?.Publish == "auto-draft")
                await tasks.RequestPublicationAsync(task.Id, runId, "auto-draft", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Task {TaskId} failed in run {RunId}", task.Id, runId);
            await MarkFailedAsync(context, ex.Message, cancellationToken);
            await tasks.CloseExecutionAsync(runId, ExecutionStatus.Failed, ex.Message, cancellationToken);
        }
    }

    /// <summary>Runs one step and, on any non-success outcome, applies the matching state transition and closes the run.</summary>
    /// <returns><see langword="true"/> if the pipeline should continue to the next step.</returns>
    private async Task<bool> RunStepAsync(IPipelineStep step, PipelineContext context, CancellationToken cancellationToken)
    {
        var result = await step.ExecuteAsync(context, cancellationToken);
        switch (result.Outcome)
        {
            case PipelineOutcome.Succeeded:
                return true;
            case PipelineOutcome.WaitingForQuota:
                await TransitionAsync(context, FactoryTaskStatus.WaitingForQuota, result.Reason, cancellationToken);
                await tasks.CompleteRunAsync(context.RunId, ExecutionStatus.Failed, cancellationToken);
                return false;
            case PipelineOutcome.NeedsHuman:
                await TransitionAsync(context, FactoryTaskStatus.NeedsHuman, result.Reason, cancellationToken);
                await tasks.CompleteRunAsync(context.RunId, ExecutionStatus.Succeeded, cancellationToken);
                return false;
            case PipelineOutcome.Failed:
            default:
                await TransitionAsync(context, FactoryTaskStatus.Failed, result.Reason, cancellationToken);
                await tasks.CompleteRunAsync(context.RunId, ExecutionStatus.Failed, cancellationToken);
                return false;
        }
    }

    private async Task TransitionAsync(PipelineContext context, FactoryTaskStatus next, string? reason, CancellationToken cancellationToken)
    {
        await tasks.TransitionAsync(context.Task.Id, context.CurrentStatus, next, reason, cancellationToken);
        context.CurrentStatus = next;
    }

    private async Task MarkFailedAsync(PipelineContext context, string error, CancellationToken cancellationToken)
    {
        try { await TransitionAsync(context, FactoryTaskStatus.Failed, error, cancellationToken); }
        catch (InvalidOperationException)
        {
            // The task already left the status this executor last recorded (for example another worker
            // recovered it after a lost lease); there is nothing further this run can do.
        }
    }
}
