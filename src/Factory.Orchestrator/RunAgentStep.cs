using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>Selects which configured agent runs this attempt, invokes it, records the invocation, and interprets
/// its result contract. Agent selection (see <see cref="AgentSelector"/>) is the only agent-specific branching
/// here: everything else is written against the agent-agnostic <see cref="AgentRunResult"/> contract.</summary>
public sealed class RunAgentStep(ITaskStore tasks, AgentSelector selector, IOptions<FactoryOptions> options) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        // SF-704: an agent or preset name that doesn't match any configured profile is a misconfiguration, not a
        // transient unavailability — failing clearly here beats AgentSelector silently falling back to whatever
        // else is configured as if no preference had been set at all.
        var preferred = context.Task.PreferredAgent;
        var taskClass = context.Task.TaskClass ?? "quick";
        if (context.Task.AgentRoutingError is not null)
            return PipelineStepResult.NeedsHuman($"Agent routing configuration error: {context.Task.AgentRoutingError}");

        if (preferred is not null && !selector.KnownAgentNames.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            return PipelineStepResult.NeedsHuman(
                $"Preferred agent/preset '{preferred}' is not configured. Configured options: {string.Join(", ", selector.KnownAgentNames)}.");
        }

        if (!selector.HasSupportingAgent(taskClass))
            return PipelineStepResult.NeedsHuman($"No configured agent supports coding class '{taskClass}'.");

        var agent = await selector.SelectAsync(preferred, cancellationToken, taskClass);
        if (agent is null) return PipelineStepResult.WaitingForQuota($"No configured agent supporting coding class '{taskClass}' is available (paused, at quota, missing, or unauthenticated).");
        var selectionReason = preferred is null
            ? $"Selected '{agent.Name}' for coding class '{taskClass}' from configured provider order."
            : string.Equals(preferred, agent.Name, StringComparison.OrdinalIgnoreCase)
                ? context.Task.PreferredAgentReason ?? $"Selected the task's preferred provider '{preferred}' for coding class '{taskClass}'."
                : $"Provider fallback selected '{agent.Name}' instead of '{preferred}' for coding class '{taskClass}' because the preferred provider is paused, at quota, unavailable, unauthenticated, or does not support the class.";

        // Persisted from the moment the agent is actually selected — before it runs, not only once it finishes —
        // so a task currently mid-invocation is correctly attributed to the agent really running it, including
        // after a fallback away from the task's own PreferredAgent. Always cleared once this invocation is done,
        // whichever way it ends, so "busy" never outlives the actual invocation.
        await tasks.SetCurrentAgentAsync(context.Task.Id, agent.Name, selectionReason, cancellationToken);
        try
        {
            return await RunAsync(context, agent, selectionReason, cancellationToken);
        }
        finally
        {
            await tasks.SetCurrentAgentAsync(context.Task.Id, null, null, cancellationToken);
        }
    }

    private async Task<PipelineStepResult> RunAsync(PipelineContext context, IAgentRunner agent, string selectionReason, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "AgentImplementation", context.AttemptNumber, cancellationToken);
        var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, context.RunId, stepId);
        // A previously recorded session is only ever offered back to the *same* agent that produced it (SF-701) —
        // a fallback to a different agent (quota, pause) always gets a fresh invocation, exactly as before this
        // task, since a different provider's CLI cannot use another provider's private session id.
        var resumeSessionId = context.Task.ResumableSessionAgent == agent.Name ? context.Task.ResumableSessionId : null;
        var taskClass = context.Task.TaskClass ?? "quick";
        var result = await agent.RunAsync(new AgentRunRequest(context.Task.Id, context.RunId, stepId, context.Worktree!.Path, context.AttemptNumber, logPath, resumeSessionId, TaskClass: taskClass), cancellationToken);
        // A quota-interrupted invocation never got a real chance to implement anything, so it is excluded from
        // the implementation-attempt budget (CountAgentRunsAsync) even though it stays recorded here in full.
        await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), context.Task.Id, context.RunId, stepId, agent.Name, result.Process.StartedAt,
            result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
            result.Process.Succeeded ? "Succeeded" : "Failed", result.Process.StandardOutput, result.Process.StandardError,
            result.QuotaDetected, result.QuotaResetAt, context.AttemptNumber, result.Result?.NeedsHuman ?? false, result.Result,
            CountsAsImplementationAttempt: !result.QuotaDetected, ProviderSessionId: result.ProviderSessionId,
            Model: result.Model ?? agent.Model, ReasoningEffort: result.ReasoningEffort ?? agent.ReasoningEffort,
            SelectionReason: selectionReason, TaskClass: taskClass), cancellationToken);

        // Quota status is persisted independently of this task's run: every invocation updates it, whether or not
        // quota was detected, so a status that cleared is reflected immediately for AgentSelector rather than only
        // by re-scanning task-run history. Keyed by Provider (SF-704), matching AgentSelector's read, so every
        // preset of one provider correctly shares this same quota record instead of each keeping its own.
        await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus(agent.Provider, result.QuotaDetected, result.Window, result.ResetKind,
            result.QuotaResetAt, result.Process.CompletedAt, result.QuotaDetail), cancellationToken);

        // Independent of this invocation's outcome, exactly like quota status above: a null session id (profile
        // has resume disabled, or none was reported) correctly clears any stale pointer for this agent.
        await tasks.SetResumableSessionAsync(context.Task.Id, agent.Name, result.ProviderSessionId, cancellationToken);

        if (result.QuotaDetected)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, $"{agent.Name} quota reached", result.Process.StandardError, cancellationToken);

            // Excluding quota interruptions from the implementation-attempt budget must never let a task wait on
            // quota forever: a separate, explicitly bounded count of quota interruptions stops it with an
            // actionable reason once that bound is reached, instead of waiting again.
            var interruptions = await tasks.CountQuotaInterruptionsAsync(context.Task.Id, cancellationToken);
            var maxInterruptions = context.Configuration!.MaxQuotaInterruptions;
            if (interruptions >= maxInterruptions)
            {
                return PipelineStepResult.NeedsHuman(
                    $"Quota interruption limit ({maxInterruptions}) reached without a successful implementation attempt; a human must intervene.");
            }
            return PipelineStepResult.WaitingForQuota($"{agent.Name} quota reached");
        }
        if (!result.Process.Succeeded)
        {
            var reason = result.Process.TimedOut ? $"{agent.Name} timed out." : $"{agent.Name} exited with code {result.Process.ExitCode}.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, result.Process.StandardError, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }
        if (result.Result is null)
        {
            var reason = result.ValidationError ?? $"{agent.Name} produced no result.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }

        var agentResult = result.Result;
        if (agentResult.Status == "failed")
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, agentResult.Summary, null, cancellationToken);
            return PipelineStepResult.Failed($"Agent reported failure: {agentResult.Summary}");
        }
        if (agentResult.Status is "blocked" or "needs-human" || agentResult.NeedsHuman)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, agentResult.Summary, cancellationToken);
            var reason = agentResult.HumanReason ?? agentResult.Summary;
            return PipelineStepResult.NeedsHuman(agentResult.Status == "blocked" ? $"Agent blocked: {reason}" : reason);
        }
        if (agentResult.Status != "completed")
        {
            var reason = $"Unsupported agent status '{agentResult.Status}'.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }

        context.AgentResult = agentResult;
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, agentResult.Summary, cancellationToken);
        return PipelineStepResult.Ok;
    }
}
