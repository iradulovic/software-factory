using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Runs one claimed task through preparation, agent implementation, and independent validation.
/// Every outcome is an explicit state transition; the run and its steps are always closed.
/// </summary>
public sealed class TaskExecutor(ITaskStore tasks, IGitHubStore github, IWorktreeManager worktrees, IWorktreeInspector inspector,
    ITaskContextWriter contextWriter, IAgentRunner agent, IRepositoryConfigurationReader configurationReader,
    IProcessRunner processes, ILogger<TaskExecutor> logger)
{
    private const string AgentName = "Codex";

    public async Task ExecuteAsync(FactoryTask task, Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(task, runId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Task {TaskId} failed in run {RunId}", task.Id, runId);
            await MarkFailedAsync(task.Id, ex.Message, cancellationToken);
            await tasks.CloseExecutionAsync(runId, ExecutionStatus.Failed, ex.Message, cancellationToken);
        }
    }

    private async Task RunAsync(FactoryTask task, Guid runId, CancellationToken cancellationToken)
    {
        await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, cancellationToken);
        var repository = await github.GetRepositoryAsync(task.RepositoryId, cancellationToken) ?? throw new InvalidOperationException("Repository not found.");
        var issue = task.GitHubIssueId is { } issueId ? await github.GetIssueAsync(issueId, cancellationToken) : null;

        var prepareStep = await tasks.StartStepAsync(runId, "CreateWorktree", 1, cancellationToken);
        var location = await worktrees.CreateAsync(repository, task, cancellationToken);
        await tasks.SetWorkspaceAsync(task.Id, location.BranchName, location.Path, cancellationToken);
        await contextWriter.WriteAsync(location.Path, repository, issue, task, cancellationToken);
        await tasks.CompleteStepAsync(prepareStep, ExecutionStatus.Succeeded, null, location.Path, cancellationToken);

        // Validation settings are resolved from the base branch before the agent runs and persisted with the run,
        // so nothing the agent writes into the worktree can change how its work is validated.
        var baseRef = $"origin/{task.BaseBranch}";
        var configuration = await configurationReader.ReadAsync(location.Path, baseRef, cancellationToken);
        await tasks.SetRunConfigurationAsync(runId, configuration, cancellationToken);

        await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null, cancellationToken);
        var agentStep = await tasks.StartStepAsync(runId, "AgentImplementation", 1, cancellationToken);
        var result = await agent.RunAsync(new AgentRunRequest(task.Id, runId, agentStep, location.Path, 1), cancellationToken);
        await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), task.Id, runId, agentStep, AgentName, result.Process.StartedAt,
            result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
            result.Process.Succeeded ? "Succeeded" : "Failed", result.Process.StandardOutput, result.Process.StandardError,
            result.QuotaDetected, null, 1, result.Result?.NeedsHuman ?? false, result.Result), cancellationToken);

        if (result.QuotaDetected)
        {
            await tasks.CompleteStepAsync(agentStep, ExecutionStatus.Failed, "Quota reached", result.Process.StandardError, cancellationToken);
            await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Implementing, FactoryTaskStatus.WaitingForQuota, $"{AgentName} quota reached", cancellationToken);
            await tasks.CompleteRunAsync(runId, ExecutionStatus.Failed, cancellationToken);
            return;
        }
        if (!result.Process.Succeeded)
            throw new InvalidOperationException(result.Process.TimedOut ? $"{AgentName} timed out." : $"{AgentName} exited with code {result.Process.ExitCode}.");
        if (result.Result is null)
            throw new InvalidOperationException(result.ValidationError ?? $"{AgentName} produced no result.");

        var agentResult = result.Result;
        if (agentResult.Status == "failed")
        {
            await tasks.CompleteStepAsync(agentStep, ExecutionStatus.Failed, agentResult.Summary, null, cancellationToken);
            await FailAsync(task.Id, runId, FactoryTaskStatus.Implementing, $"Agent reported failure: {agentResult.Summary}", cancellationToken);
            return;
        }
        if (agentResult.Status is "blocked" or "needs-human" || agentResult.NeedsHuman)
        {
            await tasks.CompleteStepAsync(agentStep, ExecutionStatus.Succeeded, null, agentResult.Summary, cancellationToken);
            var reason = agentResult.HumanReason ?? agentResult.Summary;
            await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman,
                agentResult.Status == "blocked" ? $"Agent blocked: {reason}" : reason, cancellationToken);
            await tasks.CompleteRunAsync(runId, ExecutionStatus.Succeeded, cancellationToken);
            return;
        }
        if (agentResult.Status != "completed")
            throw new InvalidOperationException($"Unsupported agent status '{agentResult.Status}'.");

        if (!await inspector.HasChangesAsync(location.Path, baseRef, cancellationToken))
        {
            const string reason = "Agent reported completion but the worktree contains no changes.";
            await tasks.CompleteStepAsync(agentStep, ExecutionStatus.Failed, reason, agentResult.Summary, cancellationToken);
            await FailAsync(task.Id, runId, FactoryTaskStatus.Implementing, reason, cancellationToken);
            return;
        }
        await tasks.CompleteStepAsync(agentStep, ExecutionStatus.Succeeded, null, agentResult.Summary, cancellationToken);

        await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null, cancellationToken);
        foreach (var command in configuration.BuildCommands.Concat(configuration.TestCommands))
        {
            var stepType = configuration.BuildCommands.Contains(command) ? "Build" : "Test";
            var stepId = await tasks.StartStepAsync(runId, stepType, 1, cancellationToken);
            var process = await RunCommandAsync(command, location.Path, cancellationToken);
            await tasks.CompleteStepAsync(stepId, process.Succeeded ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
                process.Succeeded ? null : process.StandardError, process.StandardOutput, cancellationToken);
            if (!process.Succeeded)
            {
                await FailAsync(task.Id, runId, FactoryTaskStatus.Validating, $"{stepType} failed: {process.StandardError}", cancellationToken);
                return;
            }
        }
        await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Validating, FactoryTaskStatus.ReadyForPublish, null, cancellationToken);
        await tasks.TransitionAsync(task.Id, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Completed, null, cancellationToken);
        await tasks.CompleteRunAsync(runId, ExecutionStatus.Succeeded, cancellationToken);
        logger.LogInformation("Completed task {TaskId} in run {RunId}", task.Id, runId);
    }

    private async Task FailAsync(Guid taskId, Guid runId, FactoryTaskStatus from, string reason, CancellationToken cancellationToken)
    {
        await tasks.TransitionAsync(taskId, from, FactoryTaskStatus.Failed, reason, cancellationToken);
        await tasks.CompleteRunAsync(runId, ExecutionStatus.Failed, cancellationToken);
    }

    private Task<ProcessResult> RunCommandAsync(string command, string directory, CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return processes.RunAsync(new ProcessRequest(parts[0], parts[1..], directory, Timeout: TimeSpan.FromMinutes(30)), cancellationToken);
    }

    private async Task MarkFailedAsync(Guid taskId, string error, CancellationToken cancellationToken)
    {
        foreach (var state in new[] { FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating })
        {
            try { await tasks.TransitionAsync(taskId, state, FactoryTaskStatus.Failed, error, cancellationToken); return; }
            catch (InvalidOperationException) { }
        }
    }
}
