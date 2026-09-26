using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>Runs opt-in independent review and loops actionable findings back to the implementation agent.</summary>
public sealed class ReviewStep(ITaskStore tasks, AgentSelector selector, IOptions<FactoryOptions> options, ILogger<ReviewStep> logger) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var configuredReviewAgent = options.Value.ReviewPreferredAgent;
        var legacyCodexPreset = configuredReviewAgent is "Codex-Luna" or "Codex-Sol";
        var preferredReviewAgent = legacyCodexPreset ? "Codex" : configuredReviewAgent;
        var reviewTaskClass = configuredReviewAgent == "Codex-Luna" ? "quick"
            : configuredReviewAgent == "Codex-Sol" ? "deep" : options.Value.ReviewTaskClass;
        if (!selector.KnownAgentNames.Contains(preferredReviewAgent, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning("Task {TaskId} skipping review: configured review preset {Agent} is not available. Configured options: {Options}.",
                context.Task.Id, preferredReviewAgent, string.Join(", ", selector.KnownAgentNames));
            return PipelineStepResult.Ok;
        }

        var maxFixAttempts = context.Configuration!.MaxReviewFixAttempts;
        for (var fixAttempt = 0; ; fixAttempt++)
        {
            var reviewOutcome = await RunReviewWithRetriesAsync(context, preferredReviewAgent, configuredReviewAgent, reviewTaskClass,
                fixAttempt, cancellationToken);
            if (reviewOutcome.Result.Outcome != PipelineOutcome.NeedsHuman) return reviewOutcome.Result;

            var review = reviewOutcome.Review!;
            if (review.Findings.Count == 0 || fixAttempt >= maxFixAttempts)
            {
                var reason = review.HumanReason ?? review.Summary;
                return PipelineStepResult.NeedsHuman(review.Status == "blocked" ? $"Review blocked: {reason}" : reason);
            }

            var fixResult = await RunFixAsync(context, review.Findings, fixAttempt + 1, cancellationToken);
            if (fixResult.Outcome != PipelineOutcome.Succeeded) return fixResult;
            context.ReviewFixAttempts++;
        }
    }

    private async Task<ReviewOutcome> RunReviewWithRetriesAsync(PipelineContext context, string preferredReviewAgent,
        string configuredReviewAgent, string reviewTaskClass, int fixAttempt, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, context.Configuration!.MaxReviewAttempts);
        PipelineStepResult? lastFailure = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var agent = await selector.SelectAsync(preferredReviewAgent, cancellationToken, reviewTaskClass);
            if (agent is null)
            {
                logger.LogInformation("Task {TaskId} skipping review: no agent available (paused or at quota).", context.Task.Id);
                return new(PipelineStepResult.Ok, null);
            }

            var selectionReason = string.Equals(agent.Name, preferredReviewAgent, StringComparison.OrdinalIgnoreCase)
                ? $"Configured review provider '{configuredReviewAgent}' selected coding class '{reviewTaskClass}' independently of implementation."
                : $"Provider fallback selected review agent '{agent.Name}' for coding class '{reviewTaskClass}' because configured review provider '{configuredReviewAgent}' is paused, at quota, or lacks the class.";
            await tasks.SetCurrentAgentAsync(context.Task.Id, agent.Name, selectionReason, cancellationToken);
            ReviewOutcome outcome;
            try { outcome = await RunReviewAsync(context, agent, attempt, fixAttempt, reviewTaskClass, selectionReason, cancellationToken); }
            finally { await tasks.SetCurrentAgentAsync(context.Task.Id, null, null, cancellationToken); }

            if (outcome.Result.Outcome != PipelineOutcome.Failed) return outcome;
            lastFailure = outcome.Result;
        }

        logger.LogWarning("Task {TaskId} review failed after {Attempts} attempt(s) ({Reason}); skipping review.",
            context.Task.Id, maxAttempts, lastFailure?.Reason);
        return new(PipelineStepResult.Ok, null);
    }

    private async Task<ReviewOutcome> RunReviewAsync(PipelineContext context, IAgentRunner agent, int attempt, int fixAttempt,
        string taskClass, string selectionReason, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "AgentReview", attempt, cancellationToken);
        var agentRunId = Guid.NewGuid();
        logger.LogInformation("Task {TaskId} run {RunId} step {StepId} agent run {AgentRunId} starting review attempt {Attempt} after {FixAttempts} fix attempt(s)",
            context.Task.Id, context.RunId, stepId, agentRunId, attempt, fixAttempt);
        var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, context.RunId, stepId);
        var result = await agent.RunAsync(new AgentRunRequest(context.Task.Id, context.RunId, stepId, context.Worktree!.Path, attempt,
            logPath, Purpose: AgentRunPurpose.Review, TaskClass: taskClass), cancellationToken);

        var review = result.ReviewResult;
        var validReview = review?.Status is "completed" or "blocked" or "needs-human";
        await tasks.SaveAgentRunAsync(new AgentRunRecord(agentRunId, context.Task.Id, context.RunId, stepId, agent.Name,
            result.Process.StartedAt, result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
            result.Process.Succeeded && !result.QuotaDetected && validReview ? "Succeeded" : "Failed",
            result.Process.StandardOutput, result.Process.StandardError, result.QuotaDetected, result.QuotaResetAt, attempt,
            review?.NeedsHuman == true || review?.Status is "blocked" or "needs-human", null,
            CountsAsImplementationAttempt: false, ProviderSessionId: result.ProviderSessionId, Model: result.Model ?? agent.Model,
            ReasoningEffort: result.ReasoningEffort ?? agent.ReasoningEffort, SelectionReason: selectionReason, Purpose: "Review", TaskClass: taskClass), cancellationToken);
        await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus(agent.Provider, result.QuotaDetected, result.Window, result.ResetKind,
            result.QuotaResetAt, result.Process.CompletedAt, result.QuotaDetail), cancellationToken);

        if (result.QuotaDetected)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, $"{agent.Name} quota reached", result.Process.StandardError, cancellationToken);
            return new(PipelineStepResult.Failed($"{agent.Name} quota reached"), null);
        }
        if (!result.Process.Succeeded)
        {
            var reason = result.Process.TimedOut ? $"{agent.Name} timed out." : $"{agent.Name} exited with code {result.Process.ExitCode}.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, result.Process.StandardError, cancellationToken);
            return new(PipelineStepResult.Failed(reason), null);
        }
        if (review is null)
        {
            var reason = result.ValidationError ?? $"{agent.Name} produced no review result.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return new(PipelineStepResult.Failed(reason), null);
        }
        if (review.Status == "failed")
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, review.Summary, null, cancellationToken);
            return new(PipelineStepResult.Failed($"Review agent reported failure: {review.Summary}"), null);
        }

        await tasks.SaveReviewFindingsAsync(context.Task.Id, context.RunId, agent.Name, review.Findings, cancellationToken);
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, review.Summary, cancellationToken);
        return review.Status is "blocked" or "needs-human" || review.NeedsHuman
            ? new(PipelineStepResult.NeedsHuman(review.HumanReason ?? review.Summary), review)
            : new(PipelineStepResult.Ok, review);
    }

    private async Task<PipelineStepResult> RunFixAsync(PipelineContext context, IReadOnlyList<ReviewFinding> findings,
        int attempt, CancellationToken cancellationToken)
    {
        var implementationAgent = context.ImplementingAgent ?? context.Task.ResumableSessionAgent ?? context.Task.PreferredAgent;
        if (implementationAgent is null)
            return PipelineStepResult.NeedsHuman("The original implementing agent could not be identified for the review fix.");
        var taskClass = context.Task.TaskClass ?? "quick";
        var agent = await selector.SelectExactAsync(implementationAgent, cancellationToken, taskClass);
        if (agent is null)
            return PipelineStepResult.WaitingForQuota($"Original implementing agent '{implementationAgent}' is unavailable for review fix attempt {attempt}.");

        var stepId = await tasks.StartStepAsync(context.RunId, "AgentReviewFix", attempt, cancellationToken);
        var agentRunId = Guid.NewGuid();
        var selectionReason = $"Re-selected original implementing agent '{implementationAgent}' for review fix attempt {attempt}.";
        logger.LogInformation("Task {TaskId} run {RunId} step {StepId} agent run {AgentRunId} starting review fix attempt {Attempt} with {FindingCount} finding(s)",
            context.Task.Id, context.RunId, stepId, agentRunId, attempt, findings.Count);
        await tasks.SetCurrentAgentAsync(context.Task.Id, agent.Name, selectionReason, cancellationToken);
        try
        {
            var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, context.RunId, stepId);
            var result = await agent.RunAsync(new AgentRunRequest(context.Task.Id, context.RunId, stepId, context.Worktree!.Path, attempt,
                logPath, context.ImplementationSessionId, AgentRunPurpose.Fix, taskClass, findings), cancellationToken);
            var validResult = result.Result?.Status is "completed" or "blocked" or "needs-human";
            await tasks.SaveAgentRunAsync(new AgentRunRecord(agentRunId, context.Task.Id, context.RunId, stepId, agent.Name,
                result.Process.StartedAt, result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
                result.Process.Succeeded && !result.QuotaDetected && validResult ? "Succeeded" : "Failed",
                result.Process.StandardOutput, result.Process.StandardError, result.QuotaDetected, result.QuotaResetAt, attempt,
                result.Result?.NeedsHuman == true || result.Result?.Status is "blocked" or "needs-human", result.Result,
                CountsAsImplementationAttempt: false, ProviderSessionId: result.ProviderSessionId, Model: result.Model ?? agent.Model,
                ReasoningEffort: result.ReasoningEffort ?? agent.ReasoningEffort, SelectionReason: selectionReason, Purpose: "Fix", TaskClass: taskClass), cancellationToken);
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus(agent.Provider, result.QuotaDetected, result.Window, result.ResetKind,
                result.QuotaResetAt, result.Process.CompletedAt, result.QuotaDetail), cancellationToken);
            await tasks.SetResumableSessionAsync(context.Task.Id, agent.Name, result.ProviderSessionId, cancellationToken);
            context.ImplementationSessionId = result.ProviderSessionId;

            if (result.QuotaDetected)
            {
                await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, $"{agent.Name} quota reached", result.Process.StandardError, cancellationToken);
                return PipelineStepResult.WaitingForQuota($"{agent.Name} quota reached during review fix attempt {attempt}.");
            }
            if (!result.Process.Succeeded || result.Result is null || result.Result.Status == "failed")
            {
                var reason = result.ValidationError ?? result.Result?.Summary ?? (result.Process.TimedOut
                    ? $"{agent.Name} timed out." : $"{agent.Name} exited with code {result.Process.ExitCode}.");
                await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, result.Process.StandardError, cancellationToken);
                return PipelineStepResult.Failed(reason);
            }
            if (result.Result.Status is "blocked" or "needs-human" || result.Result.NeedsHuman)
            {
                await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, result.Result.Summary, cancellationToken);
                return PipelineStepResult.NeedsHuman(result.Result.HumanReason ?? result.Result.Summary);
            }
            if (result.Result.Status != "completed")
            {
                var reason = $"Unsupported agent status '{result.Result.Status}'.";
                await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
                return PipelineStepResult.Failed(reason);
            }

            context.AgentResult = result.Result;
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, result.Result.Summary, cancellationToken);
            return PipelineStepResult.Ok;
        }
        finally
        {
            await tasks.SetCurrentAgentAsync(context.Task.Id, null, null, cancellationToken);
        }
    }

    private sealed record ReviewOutcome(PipelineStepResult Result, AgentReviewResult? Review);
}
