using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

public sealed class Worker(DatabaseMigrator migrator, ITaskStore tasks, IGitHubStore github, IWorktreeManager worktrees,
    ITaskContextWriter contextWriter, IAgentRunner agent, IRepositoryConfigurationReader configurationReader,
    IProcessRunner processes, IOptions<FactoryOptions> options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await migrator.MigrateAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await tasks.ClaimNextAsync(options.Value.WorkerId, LeaseDuration, stoppingToken);
                if (task is null) { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken); continue; }
                await ExecuteWithLeaseAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker iteration failed; polling will continue");
                try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(Math.Max(2, options.Value.TaskLeaseSeconds));

    private TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(Math.Min(
        Math.Max(1, options.Value.LeaseHeartbeatSeconds),
        LeaseDuration.TotalSeconds / 2));

    private async Task ExecuteWithLeaseAsync(FactoryTask task, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = MaintainLeaseAsync(task.Id, execution, stoppingToken);
        try
        {
            await ExecuteTaskAsync(task, execution.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && execution.IsCancellationRequested)
        {
            logger.LogWarning("Stopped task {TaskId} after its lease could not be renewed", task.Id);
        }
        finally
        {
            await execution.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        }
    }

    private async Task MaintainLeaseAsync(Guid taskId, CancellationTokenSource execution, CancellationToken stoppingToken)
    {
        try
        {
            while (!execution.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, execution.Token);
                if (await tasks.RenewLeaseAsync(taskId, options.Value.WorkerId, LeaseDuration, execution.Token)) continue;
                logger.LogWarning("Lease ownership was lost for task {TaskId}", taskId);
                await execution.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Lease renewal failed for task {TaskId}; cancelling its execution", taskId);
            await execution.CancelAsync();
        }
    }

    private async Task ExecuteTaskAsync(FactoryTask task, CancellationToken cancellationToken)
    {
        var runId = await tasks.StartRunAsync(task.Id, options.Value.WorkerId, cancellationToken);
        try
        {
            await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, cancellationToken);
            var repository = await github.GetRepositoryAsync(task.RepositoryId, cancellationToken) ?? throw new InvalidOperationException("Repository not found.");
            var issue = task.GitHubIssueId is { } issueId ? await github.GetIssueAsync(issueId, cancellationToken) : null;
            var prepareStep = await tasks.StartStepAsync(runId, "CreateWorktree", 1, cancellationToken);
            var location = await worktrees.CreateAsync(repository, task, cancellationToken);
            await tasks.SetWorkspaceAsync(task.Id, location.BranchName, location.Path, cancellationToken);
            await contextWriter.WriteAsync(location.Path, repository, issue, task, cancellationToken);
            await tasks.CompleteStepAsync(prepareStep, ExecutionStatus.Succeeded, null, location.Path, cancellationToken);

            await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null, cancellationToken);
            var agentStep = await tasks.StartStepAsync(runId, "AgentImplementation", 1, cancellationToken);
            var result = await agent.RunAsync(new AgentRunRequest(task.Id, runId, agentStep, location.Path, 1), cancellationToken);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), task.Id, runId, agentStep, "Codex", result.Process.StartedAt,
                result.Process.CompletedAt, result.Process.Duration.TotalSeconds, result.Process.ExitCode,
                result.Process.Succeeded ? "Succeeded" : "Failed", result.Process.StandardOutput, result.Process.StandardError,
                result.QuotaDetected, null, 1, result.Result?.NeedsHuman ?? false, result.Result), cancellationToken);

            if (result.QuotaDetected)
            {
                await tasks.CompleteStepAsync(agentStep, ExecutionStatus.Failed, "Quota reached", result.Process.StandardError, cancellationToken);
                await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Implementing, FactoryTaskStatus.WaitingForQuota, "Codex quota reached", cancellationToken);
                await tasks.CompleteRunAsync(runId, ExecutionStatus.Failed, cancellationToken); return;
            }
            if (!result.Process.Succeeded || result.Result is null)
                throw new InvalidOperationException(result.ValidationError ?? $"Codex exited with code {result.Process.ExitCode}.");
            await tasks.CompleteStepAsync(agentStep, ExecutionStatus.Succeeded, null, result.Result.Summary, cancellationToken);
            if (result.Result.NeedsHuman || result.Result.Status == "needs-human")
            {
                await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman,
                    result.Result.HumanReason ?? result.Result.Summary, cancellationToken);
                await tasks.CompleteRunAsync(runId, ExecutionStatus.Succeeded, cancellationToken);
                return;
            }

            await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null, cancellationToken);
            var config = await configurationReader.ReadAsync(location.Path, cancellationToken);
            foreach (var command in config.BuildCommands.Concat(config.TestCommands))
            {
                var stepType = config.BuildCommands.Contains(command) ? "Build" : "Test";
                var stepId = await tasks.StartStepAsync(runId, stepType, 1, cancellationToken);
                var process = await RunCommandAsync(command, location.Path, cancellationToken);
                await tasks.CompleteStepAsync(stepId, process.Succeeded ? ExecutionStatus.Succeeded : ExecutionStatus.Failed,
                    process.Succeeded ? null : process.StandardError, process.StandardOutput, cancellationToken);
                if (!process.Succeeded) throw new InvalidOperationException($"{stepType} failed: {process.StandardError}");
            }
            await tasks.TransitionAsync(task.Id, FactoryTaskStatus.Validating, FactoryTaskStatus.ReadyForPublish, null, cancellationToken);
            await tasks.TransitionAsync(task.Id, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Completed, null, cancellationToken);
            await tasks.CompleteRunAsync(runId, ExecutionStatus.Succeeded, cancellationToken);
            logger.LogInformation("Completed task {TaskId} in run {RunId}", task.Id, runId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Task {TaskId} failed in run {RunId}", task.Id, runId);
            await MarkFailedAsync(task.Id, ex.Message, cancellationToken);
            await tasks.CompleteRunAsync(runId, ExecutionStatus.Failed, cancellationToken);
        }
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
