using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Polls independently of the main task pipeline, on its own (much longer) interval: cleanup is background
/// housekeeping, not work a task or a human is waiting on.
/// </summary>
public sealed class WorktreeCleanupWorker(WorktreeCleanupExecutor executor, IOptions<WorktreeCleanupOptions> options, ILogger<WorktreeCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await executor.RunOnceAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("Worktree cleanup removed {Count} worktree(s)", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Worktree cleanup iteration failed; polling will continue"); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
