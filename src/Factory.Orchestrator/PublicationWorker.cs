using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Polls for publication requests independently of the main task pipeline: a human clicking "Publish" (or an
/// `auto-draft` repository requesting it itself) is a separate, much shorter-lived unit of work than implementing
/// a task, and should not queue behind whatever the main <see cref="Worker"/> is currently implementing.
/// </summary>
public sealed class PublicationWorker(DatabaseMigrator migrator, ITaskStore tasks, PublicationExecutor executor,
    IOptions<FactoryOptions> options, ILogger<PublicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await migrator.MigrateAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Recovers the one interruption a reclaimed publication attempt cannot fix by itself: the pull
                // request was created and recorded successfully, but the worker crashed before the task itself
                // finished transitioning out of ReadyForPublish.
                var reconciled = await tasks.ReconcilePublishedTasksAsync(stoppingToken);
                if (reconciled > 0)
                    logger.LogInformation("Reconciled {Count} task(s) whose pull request already existed but had not completed its status transition", reconciled);

                var request = await tasks.ClaimNextPublicationAsync(options.Value.WorkerId, TimeSpan.FromSeconds(options.Value.PublicationLeaseSeconds), stoppingToken);
                if (request is null) { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken); continue; }
                await executor.ExecuteAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publication worker iteration failed; polling will continue");
                try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
