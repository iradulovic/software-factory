using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Runs an optional, opt-in second-agent review pass (SF-702), only when <see cref="PipelineContext.ReviewRequested"/>
/// is set. Selecting an agent goes through the exact same <see cref="AgentSelector"/> a review invocation never
/// bypasses pause or quota, and never invokes a provider the operator reserved or that is currently exhausted.
///
/// Bounded by <see cref="RepositoryConfiguration.MaxReviewAttempts"/>: a review agent's own invocation failing
/// that many times in a row (a process crash, an invalid result — a genuine review-infrastructure problem, not a
/// negative finding) skips review rather than blocking publication, since review is advisory, not a mandatory
/// gate — this step never fails the pipeline. The one exception is a review that completes but reports
/// <c>needsHuman</c> or a <c>blocked</c> status: exactly like an implementation agent's own such signal
/// (<see cref="RunAgentStep"/>), that stops the task for an explicit human decision instead of continuing to publish.
/// </summary>
public sealed class ReviewStep(ITaskStore tasks, AgentSelector selector, IOptions<FactoryOptions> options, ILogger<ReviewStep> logger) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, context.Configuration!.MaxReviewAttempts);
        PipelineStepResult? lastFailure = null;
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

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var agent = await selector.SelectAsync(preferredReviewAgent, cancellationToken, reviewTaskClass);
            if (agent is null)
            {
                logger.LogInformation("Task {TaskId} skipping review: no agent available (paused, at quota, unavailable, or unauthenticated).", context.Task.Id);
                return PipelineStepResult.Ok;
            }

            var selectionReason = string.Equals(agent.Name, preferredReviewAgent, StringComparison.OrdinalIgnoreCase)
                ? $"Configured review provider '{configuredReviewAgent}' selected coding class '{reviewTaskClass}' independently of implementation."
                : $"Provider fallback selected review agent '{agent.Name}' for coding class '{reviewTaskClass}' because configured review provider '{configuredReviewAgent}' is paused, at quota, unavailable, unauthenticated, or lacks the class.";
            await tasks.SetCurrentAgentAsync(context.Task.Id, agent.Name, selectionReason, cancellationToken);
            PipelineStepResult outcome;
            try { outcome = await RunAsync(context, agent, attempt, reviewTaskClass, selectionReason, cancellationToken); }
            finally { await tasks.SetCurrentAgentAsync(context.Task.Id, null, null, cancellationToken); }

            if (outcome.Outcome != PipelineOutcome.Failed) return outcome;
            lastFailure = outcome;
        }

        logger.LogWarning("Task {TaskId} review failed after {Attempts} attempt(s) ({Reason}); skipping review.",
            context.Task.Id, maxAttempts, lastFailure?.Reason);
        return PipelineStepResult.Ok;
    }

    private async Task<PipelineStepResult> RunAsync(PipelineContext context, IAgentRunner agent, int attempt, string taskClass, string selectionReason, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "AgentReview", attempt, cancellationToken);
        var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, context.RunId, stepId);
        var result = await agent.RunAsync(new AgentRunRequest(context.Task.Id, context.RunId, stepId, context.Worktree!.Path, attempt, logPath, Purpose: AgentRunPurpose.Review, TaskClass: taskClass), cancellationToken);

        var review = result.ReviewResult;
        var validReview = review?.Status is "completed" or "blocked" or "needs-human";
        await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), context.Task.Id, context.RunId, stepId, agent.Name,
            result.Process.StartedAt, result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
            result.Process.Succeeded && !result.QuotaDetected && validReview ? "Succeeded" : "Failed",
            result.Process.StandardOutput, result.Process.StandardError, result.QuotaDetected, result.QuotaResetAt, attempt,
            review?.NeedsHuman == true || review?.Status is "blocked" or "needs-human", null,
            CountsAsImplementationAttempt: false, ProviderSessionId: result.ProviderSessionId, Model: result.Model ?? agent.Model,
            ReasoningEffort: result.ReasoningEffort ?? agent.ReasoningEffort, SelectionReason: selectionReason, Purpose: "Review", TaskClass: taskClass), cancellationToken);

        // Quota status is shared with implementation invocations (AgentSelector reads the same record), so a
        // review that hits quota correctly makes that agent unavailable for the task's next implementation
        // attempt too, exactly as an implementation invocation hitting quota would. Keyed by Provider (SF-704),
        // so a review run under one preset correctly shares quota with every other preset of that provider.
        await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus(agent.Provider, result.QuotaDetected, result.Window, result.ResetKind,
            result.QuotaResetAt, result.Process.CompletedAt, result.QuotaDetail), cancellationToken);

        if (result.QuotaDetected)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, $"{agent.Name} quota reached", result.Process.StandardError, cancellationToken);
            return PipelineStepResult.Failed($"{agent.Name} quota reached");
        }
        if (!result.Process.Succeeded)
        {
            var reason = result.Process.TimedOut ? $"{agent.Name} timed out." : $"{agent.Name} exited with code {result.Process.ExitCode}.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, result.Process.StandardError, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }
        if (result.ReviewResult is null)
        {
            var reason = result.ValidationError ?? $"{agent.Name} produced no review result.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }

        review = result.ReviewResult;
        if (review.Status == "failed")
        {
            // The review invocation itself failed to produce a meaningful review (e.g. could not access
            // something it needed) — this is a review-infrastructure problem, bounded and retried like any
            // other invocation failure above, never a verdict on the code under review.
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, review.Summary, null, cancellationToken);
            return PipelineStepResult.Failed($"Review agent reported failure: {review.Summary}");
        }

        await tasks.SaveReviewFindingsAsync(context.Task.Id, context.RunId, agent.Name, review.Findings, cancellationToken);
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, review.Summary, cancellationToken);

        if (review.Status is "blocked" or "needs-human" || review.NeedsHuman)
        {
            var reason = review.HumanReason ?? review.Summary;
            return PipelineStepResult.NeedsHuman(review.Status == "blocked" ? $"Review blocked: {reason}" : reason);
        }
        return PipelineStepResult.Ok;
    }
}
