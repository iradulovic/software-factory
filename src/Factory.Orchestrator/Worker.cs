using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

public sealed class Worker(DatabaseMigrator migrator, ITaskStore tasks, TaskExecutor executor, LeaseMonitor leases,
    IEnumerable<IAgentRunner> agents, IOptions<FactoryOptions> options, ILogger<Worker> logger) : BackgroundService
{
    // The configured runners never change over the process lifetime, so their names are captured once rather
    // than re-derived from the DI-resolved sequence on every poll iteration.
    private readonly IReadOnlyList<string> _configuredAgents = agents.Select(a => a.Name).ToList();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await migrator.MigrateAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await tasks.RecordHeartbeatAsync(options.Value.WorkerId, Environment.MachineName, null, stoppingToken);
                var resumed = await tasks.ResumeExpiredQuotaTasksAsync(_configuredAgents, stoppingToken);
                if (resumed > 0) logger.LogInformation("Resumed {Count} task(s) now that a configured provider is available", resumed);

                // A prerequisite that will never merge (Rejected/Cancelled/Failed) must never leave its
                // dependent silently queued forever, nor silently released to run anyway — moved to NeedsHuman
                // for an explicit operator decision instead.
                var blocked = await tasks.BlockDependentsOnFailedPrerequisitesAsync(stoppingToken);
                if (blocked > 0) logger.LogInformation("Moved {Count} task(s) to NeedsHuman: a prerequisite ended without merging", blocked);

                // Pausing stops new dispatch only, checked right here before claiming — never mid-task, so a task
                // already claimed and executing always finishes undisturbed. Publication (PublicationWorker) is
                // a separate, independently polling worker and is deliberately untouched by this: pushing and
                // opening a pull request for already-validated work consumes no coding agent's subscription.
                if (await tasks.IsDispatchPausedAsync(stoppingToken)) { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken); continue; }

                var task = await tasks.ClaimNextAsync(options.Value.WorkerId, leases.LeaseDuration, stoppingToken);
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

    private async Task ExecuteWithLeaseAsync(FactoryTask task, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var runId = await tasks.StartRunAsync(task.Id, options.Value.WorkerId, stoppingToken);
        await tasks.RecordHeartbeatAsync(options.Value.WorkerId, Environment.MachineName, task.Id, stoppingToken);
        var heartbeat = leases.MonitorAsync(task.Id, execution);
        var interrupted = false;
        try
        {
            await executor.ExecuteAsync(task, runId, execution.Token);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            interrupted = true;
        }
        finally
        {
            await execution.CancelAsync();
            try { await heartbeat; }
            catch (OperationCanceledException) { }
        }
        if (!interrupted) return;

        // An interrupted execution is closed explicitly so no run or step stays "Running" forever. Only rows still
        // running are touched: a worker that already recovered the task has closed them itself.
        if (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Worker is stopping; releasing task {TaskId} for recovery", task.Id);
            await tasks.CloseExecutionAsync(runId, ExecutionStatus.Cancelled, "Worker stopped; execution interrupted", CancellationToken.None);
            await tasks.ReleaseLeaseAsync(task.Id, options.Value.WorkerId, CancellationToken.None);
        }
        else
        {
            logger.LogWarning("Stopped task {TaskId} after its lease could not be kept", task.Id);
            await tasks.CloseExecutionAsync(runId, ExecutionStatus.Cancelled, "Lease ownership lost; execution cancelled", CancellationToken.None);
        }
    }
}
