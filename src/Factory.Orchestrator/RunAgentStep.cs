using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>Invokes the coding agent, records the invocation, and interprets its result contract.</summary>
public sealed class RunAgentStep(ITaskStore tasks, IAgentRunner agent) : IPipelineStep
{
    private const string AgentName = "Codex";

    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "AgentImplementation", context.AttemptNumber, cancellationToken);
        var result = await agent.RunAsync(new AgentRunRequest(context.Task.Id, context.RunId, stepId, context.Worktree!.Path, context.AttemptNumber), cancellationToken);
        await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), context.Task.Id, context.RunId, stepId, AgentName, result.Process.StartedAt,
            result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
            result.Process.Succeeded ? "Succeeded" : "Failed", result.Process.StandardOutput, result.Process.StandardError,
            result.QuotaDetected, result.QuotaResetAt, context.AttemptNumber, result.Result?.NeedsHuman ?? false, result.Result), cancellationToken);

        if (result.QuotaDetected)
        {
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, "Quota reached", result.Process.StandardError, cancellationToken);
            return PipelineStepResult.WaitingForQuota($"{AgentName} quota reached");
        }
        if (!result.Process.Succeeded)
        {
            var reason = result.Process.TimedOut ? $"{AgentName} timed out." : $"{AgentName} exited with code {result.Process.ExitCode}.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, result.Process.StandardError, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }
        if (result.Result is null)
        {
            var reason = result.ValidationError ?? $"{AgentName} produced no result.";
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
