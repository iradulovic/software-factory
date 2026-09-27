using System.Diagnostics;
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
    SmokeTestStep smokeTest,
    SyncBaseBranchStep syncBaseBranch,
    PreparePublicationStep preparePublication,
    ReviewStep review,
    TaskGitHubNotifier notifier,
    IWorktreeInspector inspector,
    IRepositoryConfigurationReader configurationReader,
    ILogger<TaskExecutor> logger)
{
    public async Task ExecuteAsync(FactoryTask task, Guid runId, CancellationToken cancellationToken)
    {
        using var activity = FactoryTelemetry.Source.StartActivity("task.execute");
        activity?.SetTag("factory.task_id", task.Id);
        activity?.SetTag("factory.run_id", runId);
        activity?.SetTag("factory.repository_id", task.RepositoryId);
        if (task.IssueNumber is { } issueNumber) activity?.SetTag("factory.issue_number", issueNumber);

        var context = new PipelineContext(task, runId);
        try
        {
            await TransitionAsync(context, FactoryTaskStatus.Preparing, null, cancellationToken);
            if (!await RunStepAsync(prepareRepository, context, cancellationToken)) return;
            if (task.PostImplementationRequestId is { } requestId)
            {
                var request = await tasks.GetPostImplementationRequestAsync(task.Id, cancellationToken);
                if (request?.Id != requestId || request.Kind != "verification" ||
                    string.IsNullOrWhiteSpace(task.WorktreePath) || string.IsNullOrWhiteSpace(task.BranchName) ||
                    !Directory.Exists(task.WorktreePath))
                    throw new InvalidOperationException("Verified workspace is unavailable; continue with feedback for a new implementation attempt.");
                var summary = await inspector.SummarizeAsync(task.WorktreePath, context.BaseRef, cancellationToken);
                if (!summary.IsClean || summary.CurrentBranch != request.BranchName ||
                    summary.CurrentBranch != task.BranchName ||
                    summary.HeadCommit != (request.ContinuationHeadCommit ?? request.HeadCommit))
                    throw new InvalidOperationException("Verified branch or commit changed; continue with feedback for a new implementation attempt.");
                context.Worktree = new WorktreeLocation(task.BranchName, task.WorktreePath);
                context.AgentResult = request.AgentResult;
                context.ImplementingAgent = request.AgentName;
                context.ImplementationSessionId = request.ProviderSessionId;
                if (Enum.TryParse<AgentRunPurpose>(request.AgentPurpose, out var purpose)) context.AgentPurpose = purpose;
                context.Configuration = await configurationReader.ReadAsync(task.WorktreePath, context.BaseRef, cancellationToken);
                await tasks.SetRunConfigurationAsync(runId, context.Configuration, cancellationToken);
            }
            else
            {
                await notifier.NotifyStartedAsync(context, cancellationToken);
                if (!await RunStepAsync(createWorktree, context, cancellationToken)) return;
                if (!await RunStepAsync(writeContext, context, cancellationToken)) return;
            }

            await TransitionAsync(context, FactoryTaskStatus.Implementing, null, cancellationToken);
            if (task.PostImplementationRequestId is null && !await RunStepAsync(runAgent, context, cancellationToken)) return;
            if (!await RunStepAsync(collectDiff, context, cancellationToken)) return;

            await TransitionAsync(context, FactoryTaskStatus.Validating, null, cancellationToken);
            if (!await RunStepAsync(validate, context, cancellationToken)) return;
            // SF-703: skipped entirely unless this repository opted in (RepositoryConfiguration.SmokeTest set).
            if (!await RunStepAsync(smokeTest, context, cancellationToken)) return;
            if (!await RunStepAsync(syncBaseBranch, context, cancellationToken)) return;
            if (task.PostImplementationRequestId is not null && context.BaseBranchSynchronized)
            {
                var synchronized = await inspector.SummarizeAsync(context.Worktree!.Path, context.BaseRef, cancellationToken);
                if (!synchronized.IsClean || synchronized.CurrentBranch != context.Worktree.BranchName)
                    throw new InvalidOperationException("Base synchronization left the verified branch in an unexpected state.");
                await tasks.AdvancePostImplementationHeadAsync(task.Id, synchronized.HeadCommit, cancellationToken);
            }
            if (context.BaseBranchSynchronized)
            {
                // The first validation preceded the merge. A changed branch must independently pass every
                // publication check, and a failure here needs a human decision instead of an implementation retry.
                if (!await RunStepAsync(validate, context, cancellationToken, baseSyncValidation: true)) return;
                if (!await RunStepAsync(smokeTest, context, cancellationToken, baseSyncValidation: true)) return;
            }
            if (!await RunStepAsync(preparePublication, context, cancellationToken)) return;

            // SF-702: an optional, opt-in second-agent review pass — only entered when PreparePublicationStep
            // decided this task requested one. Skipped entirely otherwise, so most tasks go straight from
            // Validating to ReadyForPublish exactly as before this task.
            if (context.ReviewRequested)
            {
                await TransitionAsync(context, FactoryTaskStatus.Reviewing, null, cancellationToken);
                if (!await RunStepAsync(review, context, cancellationToken)) return;
                // A review fix changes the commit that was validated before review. Re-run every deterministic
                // publication prerequisite against the final, re-reviewed commit so the persisted validated HEAD
                // and change summary cannot point at the pre-fix implementation.
                if (context.ReviewFixAttempts > 0)
                {
                    if (!await RunStepAsync(collectDiff, context, cancellationToken)) return;
                    if (!await RunStepAsync(validate, context, cancellationToken)) return;
                    if (!await RunStepAsync(smokeTest, context, cancellationToken)) return;
                    if (!await RunStepAsync(preparePublication, context, cancellationToken)) return;
                }
            }

            // ReadyForPublish is a resting state: a validated implementation waits here for a human (or, for an
            // auto-draft repository, the orchestrator's own request below) to actually publish it. The orchestrator
            // never assigns Completed on its own; only GitHub sync observing a merged pull request does that.
            await TransitionAsync(context, FactoryTaskStatus.ReadyForPublish, null, cancellationToken);
            await tasks.CompleteRunAsync(runId, ExecutionStatus.Succeeded, cancellationToken);
            logger.LogInformation("Task {TaskId} is ready for publish in run {RunId}", task.Id, runId);
            await notifier.NotifyReadyForPublishAsync(context, cancellationToken);

            if (context.Configuration?.Publish == "auto-draft" || context.AgentPurpose == AgentRunPurpose.MergeConflict)
                await tasks.RequestPublicationAsync(task.Id, runId,
                    context.AgentPurpose == AgentRunPurpose.MergeConflict ? MergeConflictRepair.CreatedBy : "auto-draft",
                    cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The operator may persist a stop request between two steps, racing the next transition. Treat that
            // expected lost-state race like token cancellation so it cannot be reported as a failed task or run.
            var cancellationRequested = false;
            try { cancellationRequested = await tasks.IsCancellationRequestedAsync(task.Id, CancellationToken.None); }
            catch (Exception checkError) when (checkError is not OperationCanceledException)
            {
                logger.LogDebug(checkError, "Could not confirm a stop request after task {TaskId} failed", task.Id);
            }
            if (cancellationRequested)
                throw new OperationCanceledException($"Task {task.Id} has a persisted stop request.", ex, cancellationToken);

            logger.LogError(ex, "Task {TaskId} failed in run {RunId}", task.Id, runId);
            await MarkFailedAsync(context, ex.Message, cancellationToken);
            await tasks.CloseExecutionAsync(runId, ExecutionStatus.Failed, ex.Message, cancellationToken);
            await notifier.NotifyFailedAsync(context, ex.Message, cancellationToken);
        }
    }

    /// <summary>Runs one step and, on any non-success outcome, applies the matching state transition and closes the run.</summary>
    /// <returns><see langword="true"/> if the pipeline should continue to the next step.</returns>
    private async Task<bool> RunStepAsync(IPipelineStep step, PipelineContext context, CancellationToken cancellationToken,
        bool baseSyncValidation = false)
    {
        using var activity = FactoryTelemetry.Source.StartActivity(step.GetType().Name);
        activity?.SetTag("factory.task_id", context.Task.Id);
        activity?.SetTag("factory.run_id", context.RunId);

        var result = await step.ExecuteAsync(context, cancellationToken);
        if (baseSyncValidation && result.Outcome == PipelineOutcome.Failed)
            result = PipelineStepResult.NeedsHuman($"Base branch synchronization completed, but validation failed: {result.Reason}");
        switch (result.Outcome)
        {
            case PipelineOutcome.Succeeded:
                return true;
            case PipelineOutcome.WaitingForQuota:
                await TransitionAsync(context, FactoryTaskStatus.WaitingForQuota, result.Reason, cancellationToken);
                await tasks.CompleteRunAsync(context.RunId, ExecutionStatus.Failed, cancellationToken);
                return false;
            case PipelineOutcome.NeedsHuman:
                if (context.PendingHumanRequest is { } humanRequest)
                {
                    await tasks.PauseForAgentHumanRequestAsync(context.Task.Id, context.RunId, humanRequest.AgentRunId,
                        humanRequest.Request, result.Reason ?? humanRequest.Request.Prompt,
                        humanRequest.Branch, humanRequest.Head, cancellationToken);
                    context.CurrentStatus = FactoryTaskStatus.NeedsHuman;
                    context.PendingHumanRequest = null;
                }
                else
                {
                    await TransitionAsync(context, FactoryTaskStatus.NeedsHuman, result.Reason, cancellationToken);
                    await tasks.CompleteRunAsync(context.RunId, ExecutionStatus.Succeeded, cancellationToken);
                }
                await notifier.NotifyNeedsHumanAsync(context, result.Reason ?? "No reason given.", cancellationToken);
                return false;
            case PipelineOutcome.Failed:
            default:
                // A repairable failure (SF-606) — a genuine code problem, not a broken environment — gets one
                // automatic repair attempt per remaining slot in the implementation-attempt budget: the run
                // closes as Failed (this attempt genuinely did fail) but the task itself goes straight back to
                // Pending, exactly as if a human had clicked Retry, so Worker's next poll cycle reclaims it into
                // a fresh run. WriteContextStep's own budget check (using the same CountAgentRunsAsync) is what
                // actually prevents a runaway loop; context.AttemptNumber here is that same count, already known.
                if (result.Repairable && context.AttemptNumber < context.Configuration!.MaxImplementationAttempts)
                {
                    await TransitionAsync(context, FactoryTaskStatus.Failed, result.Reason, cancellationToken);
                    await tasks.CompleteRunAsync(context.RunId, ExecutionStatus.Failed, cancellationToken);
                    var nextAttempt = context.AttemptNumber + 1;
                    await TransitionAsync(context, FactoryTaskStatus.Pending,
                        $"Automatic repair scheduled: attempt {nextAttempt} of {context.Configuration.MaxImplementationAttempts}. Previous failure: {result.Reason}", cancellationToken);
                    logger.LogInformation("Task {TaskId} failed validation ({Reason}); automatically rescheduling repair attempt {Next} of {Max}",
                        context.Task.Id, result.Reason, nextAttempt, context.Configuration.MaxImplementationAttempts);
                    return false;
                }

                var terminalReason = result.Repairable
                    ? $"{result.Reason} Implementation attempt limit ({context.Configuration!.MaxImplementationAttempts}) reached; this task will not be retried automatically."
                    : result.Reason;
                await TransitionAsync(context, FactoryTaskStatus.Failed, terminalReason, cancellationToken);
                await tasks.CompleteRunAsync(context.RunId, ExecutionStatus.Failed, cancellationToken);
                await notifier.NotifyFailedAsync(context, terminalReason ?? "No reason given.", cancellationToken);
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
