using System.Diagnostics;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.GitHubSync;

public sealed class Worker(DatabaseMigrator migrator, IGitHubStore store, IGitHubClient client, ITaskStore tasks,
    IClock clock, IOptions<GitHubSyncOptions> options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await migrator.MigrateAsync(stoppingToken);
        foreach (var configured in options.Value.Repositories)
            await store.UpsertRepositoryAsync(new GitHubRepository(0, configured.Owner, configured.Name, configured.CloneUrl, configured.DefaultBranch, configured.Enabled), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var repository in await store.GetEnabledRepositoriesAsync(stoppingToken))
                {
                    using var repositoryActivity = FactoryTelemetry.Source.StartActivity("github.sync_repository");
                    repositoryActivity?.SetTag("factory.repository_id", repository.Id);
                    try
                    {
                        // Recorded before fetching anything, not after: an issue updated mid-sync might already be
                        // behind us in the ascending-sorted page order, so using the start time as the next
                        // checkpoint guarantees it is safely re-fetched next cycle rather than silently skipped.
                        var syncStartedAt = clock.UtcNow;
                        var imported = 0; var created = 0; var cancelled = 0;
                        foreach (var issue in await client.GetIssuesAsync(repository, repository.LastSyncedAt, stoppingToken))
                        {
                            var saved = await store.UpsertIssueAsync(repository.Id, issue, stoppingToken);
                            imported++;
                            if (await tasks.CreateForIssueIfEligibleAsync(saved, repository.DefaultBranch, stoppingToken))
                            {
                                using var taskActivity = FactoryTelemetry.Source.StartActivity("github.create_task_for_issue");
                                taskActivity?.SetTag("factory.repository_id", repository.Id);
                                taskActivity?.SetTag("factory.issue_id", saved.Id);
                                created++;
                                continue;
                            }
                            var reason = !issue.State.Equals("OPEN", StringComparison.OrdinalIgnoreCase)
                                ? "Issue was closed on GitHub."
                                : !issue.Labels.Contains("factory:ready", StringComparer.OrdinalIgnoreCase) ? "The factory:ready label was removed on GitHub." : null;
                            if (reason is not null && await tasks.CancelPendingForIssueAsync(saved.Id, reason, stoppingToken)) cancelled++;
                        }
                        await store.MarkRepositorySyncedAsync(repository.Id, syncStartedAt, stoppingToken);
                        logger.LogInformation("Synchronized {Repository}; imported {IssueCount} issues, created {TaskCount} tasks, cancelled {CancelledCount} pending tasks", $"{repository.Owner}/{repository.Name}", imported, created, cancelled);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await store.RecordRepositorySyncFailureAsync(repository.Id, ex.Message, stoppingToken);
                        logger.LogError(ex, "GitHub synchronization failed for {Repository}", $"{repository.Owner}/{repository.Name}");
                    }
                }

                await ResolvePublishedTasksAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "GitHub synchronization cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>Resolves each task resting in <see cref="FactoryTaskStatus.Published"/> to <see cref="FactoryTaskStatus.Completed"/>
    /// once its pull request is merged, or <see cref="FactoryTaskStatus.Rejected"/> once it is closed without merge.
    /// A pull request that is still open is left untouched.</summary>
    private async Task ResolvePublishedTasksAsync(CancellationToken cancellationToken)
    {
        foreach (var published in await tasks.GetPublishedTasksAsync(cancellationToken))
        {
            using var activity = FactoryTelemetry.Source.StartActivity("github.resolve_published_task");
            activity?.SetTag("factory.task_id", published.TaskId);
            try
            {
                var state = await client.GetPullRequestStateAsync(published.RepositoryOwner, published.RepositoryName, published.PullRequestNumber, cancellationToken);
                if (state is null) continue;
                if (state.Merged)
                    await tasks.TransitionAsync(published.TaskId, FactoryTaskStatus.Published, FactoryTaskStatus.Completed, null, cancellationToken);
                else if (state.Closed)
                    await tasks.TransitionAsync(published.TaskId, FactoryTaskStatus.Published, FactoryTaskStatus.Rejected, "Pull request closed without merge.", cancellationToken);
                else
                    // Still open: synchronize CI status for exactly the commit GitHub reports as this PR's
                    // current head (SF-614) — fetched together in one call, so a check result can never be
                    // attributed to an older, since-superseded head (e.g. after an SF-613 continuation republished).
                    await SyncCiStatusAsync(published, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to resolve pull request outcome for task {TaskId}", published.TaskId);
            }
        }
    }

    /// <summary>Fetches and persists CI status for one still-open published pull request. A read failure
    /// (authentication, network, missing permissions) is recorded explicitly as <see cref="PullRequestCiStatus.Unavailable"/>
    /// with its error text, never silently skipped or conflated with "no checks configured."</summary>
    private async Task SyncCiStatusAsync(PublishedTaskRef published, CancellationToken cancellationToken)
    {
        using var activity = FactoryTelemetry.Source.StartActivity("github.sync_ci_status");
        activity?.SetTag("factory.task_id", published.TaskId);
        var result = await client.GetPullRequestChecksAsync(published.RepositoryOwner, published.RepositoryName, published.PullRequestNumber, cancellationToken);
        await tasks.SetCiStatusAsync(published.TaskId, PullRequestCiStatus.Overall(result), result.HeadSha, result.Checks, result.Error, cancellationToken);
    }
}
