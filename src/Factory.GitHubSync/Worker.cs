using System.Diagnostics;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.GitHubSync;

public sealed class Worker(DatabaseMigrator migrator, IGitHubStore store, IGitHubClient client, IGitHubPublisher publisher,
    ITaskStore tasks, IClock clock, IOptions<GitHubSyncOptions> options, ILogger<Worker> logger) : BackgroundService
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
                        // Backdated by SyncCheckpoint.SafetyMargin rather than persisted as-is: GitHub's search API
                        // (see GetIssuesAsync) is eventually consistent, so an issue that changed just before
                        // syncStartedAt can miss this cycle's results because it isn't indexed yet. Without the
                        // margin, the checkpoint would advance past it anyway and it would never be fetched again.
                        await store.MarkRepositorySyncedAsync(repository.Id, SyncCheckpoint.From(syncStartedAt), stoppingToken);
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
                {
                    // Still open: synchronize CI status for exactly the commit GitHub reports as this PR's
                    // current head (SF-614) — fetched together in one call, so a check result can never be
                    // attributed to an older, since-superseded head (e.g. after an SF-613 continuation republished).
                    var overallStatus = await SyncCiStatusAsync(published, cancellationToken);
                    // SF-709: a task whose own policy allows automatic merge gets one merged the moment its exact
                    // head commit's CI is green — never on a pending or failing status. A HUMAN REVIEW task is
                    // never touched here; it waits on a human merge exactly as every task did before SF-709.
                    if (!published.RequireHumanMerge && overallStatus == PullRequestCiStatus.Success)
                        await AttemptAutoMergeAsync(published, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to resolve pull request outcome for task {TaskId}", published.TaskId);
            }
        }
    }

    /// <summary>Fetches and persists CI status for one still-open published pull request. A read failure
    /// (authentication, network, missing permissions) is recorded explicitly as <see cref="PullRequestCiStatus.Unavailable"/>
    /// with its error text, never silently skipped or conflated with "no checks configured." Returns the overall
    /// status just persisted, so the caller can decide whether an SF-709 automatic merge applies without a second
    /// read of what was just written.</summary>
    private async Task<string> SyncCiStatusAsync(PublishedTaskRef published, CancellationToken cancellationToken)
    {
        using var activity = FactoryTelemetry.Source.StartActivity("github.sync_ci_status");
        activity?.SetTag("factory.task_id", published.TaskId);
        var result = await client.GetPullRequestChecksAsync(published.RepositoryOwner, published.RepositoryName, published.PullRequestNumber, cancellationToken);
        var overallStatus = PullRequestCiStatus.Overall(result);
        await tasks.SetCiStatusAsync(published.TaskId, overallStatus, result.HeadSha, result.Checks, result.Error, cancellationToken);
        return overallStatus;
    }

    /// <summary>Requests GitHub merge a task's pull request now that its own policy allows automatic merge and CI
    /// on its exact head commit is green (SF-709). On success, nothing further happens here — the next poll's own
    /// <see cref="GetPullRequestStateAsync"/> check at the top of <see cref="ResolvePublishedTasksAsync"/> observes
    /// the merge and transitions the task to <see cref="FactoryTaskStatus.Completed"/> exactly as it already does
    /// for a human-initiated merge, so no separate terminal-state logic is needed here. A rejection (a conflict, a
    /// protected-branch rule, insufficient reviews) moves the task to <see cref="FactoryTaskStatus.NeedsHuman"/>
    /// instead of retrying forever; the task leaving <see cref="FactoryTaskStatus.Published"/> this way also
    /// removes it from the next poll's <see cref="GetPublishedTasksAsync"/> result, so a rejected merge is
    /// attempted at most once.</summary>
    private async Task AttemptAutoMergeAsync(PublishedTaskRef published, CancellationToken cancellationToken)
    {
        using var activity = FactoryTelemetry.Source.StartActivity("github.auto_merge");
        activity?.SetTag("factory.task_id", published.TaskId);
        var result = await publisher.MergePullRequestAsync(published.RepositoryOwner, published.RepositoryName, published.PullRequestNumber, cancellationToken);
        if (result.Succeeded)
        {
            logger.LogInformation("Requested automatic merge for task {TaskId}'s pull request", published.TaskId);
            return;
        }

        logger.LogWarning("Automatic merge failed for task {TaskId}: {Error}", published.TaskId, result.Error);
        try
        {
            await tasks.TransitionAsync(published.TaskId, FactoryTaskStatus.Published, FactoryTaskStatus.NeedsHuman,
                $"Automatic merge failed: {result.Error}", cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The task already left Published through some other authoritative action (a human merged or closed
            // it concurrently) between this poll's state check and the merge attempt above; nothing further to do.
        }
    }
}
