using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>Selects which configured agent runs this attempt, invokes it, records the invocation, and interprets
/// its result contract. Agent selection (see <see cref="AgentSelector"/>) is the only agent-specific branching
/// here: everything else is written against the agent-agnostic <see cref="AgentRunResult"/> contract.</summary>
public sealed class RunAgentStep(ITaskStore tasks, AgentSelector selector) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var agent = await selector.SelectAsync(context.Task.PreferredAgent, cancellationToken);
        if (agent is null) return PipelineStepResult.WaitingForQuota("All configured agents are at quota.");

        var stepId = await tasks.StartStepAsync(context.RunId, "AgentImplementation", context.AttemptNumber, cancellationToken);
        var result = await agent.RunAsync(new AgentRunRequest(context.Task.Id, context.RunId, stepId, context.Worktree!.Path, context.AttemptNumber), cancellationToken);
        await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), context.Task.Id, context.RunId, stepId, agent.Name, result.Process.StartedAt,
            result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
            result.Process.Succeeded ? "Succeeded" : "Failed", result.Process.StandardOutput, result.Process.StandardError,
            result.QuotaDetected, result.QuotaResetAt, context.AttemptNumber, result.Result?.NeedsHuman ?? false, result.Result), cancellationToken);

        if (result.QuotaDetected)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, $"{agent.Name} quota reached", result.Process.StandardError, cancellationToken);
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
