using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Renews the worker's lease on a task while it executes. Losing ownership cancels execution immediately;
/// a renewal that merely fails (for example while PostgreSQL restarts) is retried until the lease would truly
/// have expired, so a transient outage does not kill a long agent run.
/// </summary>
public sealed class LeaseMonitor(ITaskStore tasks, IClock clock, IOptions<FactoryOptions> options, ILogger<LeaseMonitor> logger)
{
    private static readonly TimeSpan CancellationCheckInterval = TimeSpan.FromSeconds(1);

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(Math.Max(2, options.Value.TaskLeaseSeconds));

    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(Math.Min(
        Math.Max(1, options.Value.LeaseHeartbeatSeconds),
        LeaseDuration.TotalSeconds / 2));

    public async Task MonitorAsync(Guid taskId, CancellationTokenSource execution)
    {
        var expiresAt = clock.UtcNow + LeaseDuration;
        var renewAt = clock.UtcNow + HeartbeatInterval;
        try
        {
            while (!execution.IsCancellationRequested)
            {
                await Task.Delay(CancellationCheckInterval, execution.Token);
                try
                {
                    if (await tasks.IsCancellationRequestedAsync(taskId, execution.Token))
                    {
                        logger.LogInformation("Task {TaskId} has a persisted stop request; cancelling its execution", taskId);
                        await execution.CancelAsync();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed stop check is not evidence of a stop request. Lease renewal below remains the
                    // authoritative ownership check and will still cancel if the database stays unavailable.
                    logger.LogDebug(ex, "Could not check for a stop request on task {TaskId}", taskId);
                }

                if (clock.UtcNow < renewAt) continue;
                try
                {
                    if (await tasks.RenewLeaseAsync(taskId, options.Value.WorkerId, LeaseDuration, execution.Token))
                    {
                        expiresAt = clock.UtcNow + LeaseDuration;
                        renewAt = clock.UtcNow + HeartbeatInterval;
                        await tasks.RecordHeartbeatAsync(options.Value.WorkerId, Environment.MachineName, taskId, execution.Token);
                        continue;
                    }
                    logger.LogWarning("Lease ownership was lost for task {TaskId}; cancelling its execution", taskId);
                    await execution.CancelAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (clock.UtcNow < expiresAt)
                    {
                        renewAt = clock.UtcNow + HeartbeatInterval;
                        logger.LogWarning(ex, "Lease renewal failed for task {TaskId}; retrying until the lease expires at {ExpiresAt}", taskId, expiresAt);
                        continue;
                    }
                    logger.LogError(ex, "Lease for task {TaskId} could not be renewed before it expired; cancelling its execution", taskId);
                    await execution.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
    }
}
