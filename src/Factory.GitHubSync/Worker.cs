using System.Diagnostics;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.GitHubSync;

public sealed class Worker(DatabaseMigrator migrator, IGitHubStore store, IGitHubClient client, IGitHubPublisher publisher,
    ITaskStore tasks, ITrackerFileSync trackerFileSync, IClock clock, IOptions<GitHubSyncOptions> options, IOptions<FactoryOptions> factoryOptions, ILogger<Worker> logger) : BackgroundService
{
    // Distinct from the orchestrator's own heartbeat (which shares the same FactoryOptions:WorkerId default,
    // {machine}-{pid}, unique per process) so GET /api/workers can tell the two apart at a glance (SF-615) — this
    // host had no liveness signal at all before, only a per-repository "did the last sync fail" one.
    private string SyncWorkerId => $"sync-{factoryOptions.Value.WorkerId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await migrator.MigrateAsync(stoppingToken);
        foreach (var configured in options.Value.Repositories)
            await store.UpsertRepositoryAsync(new GitHubRepository(0, configured.Owner, configured.Name, configured.CloneUrl, configured.DefaultBranch, configured.Enabled), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await tasks.RecordHeartbeatAsync(SyncWorkerId, Environment.MachineName, null, stoppingToken);
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

                            await ReconcileIssueDependenciesAsync(repository, saved, stoppingToken);
                        }
                        // Backdated by SyncCheckpoint.SafetyMargin rather than persisted as-is: GitHub's search API
                        // (see GetIssuesAsync) is eventually consistent, so an issue that changed just before
                        // syncStartedAt can miss this cycle's results because it isn't indexed yet. Without the
                        // margin, the checkpoint would advance past it anyway and it would never be fetched again.
                        await store.MarkRepositorySyncedAsync(repository.Id, SyncCheckpoint.From(syncStartedAt), stoppingToken);
                        logger.LogInformation("Synchronized {Repository}; imported {IssueCount} issues, created {TaskCount} tasks, cancelled {CancelledCount} pending tasks", $"{repository.Owner}/{repository.Name}", imported, created, cancelled);

                        await SyncTrackerFileAsync(repository, stoppingToken);
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

    /// <summary>Parses <paramref name="issue"/>'s body for its own <c>Depends on #N</c>/<c>Blocked by #N</c>
    /// convention (SF-710) and reconciles the result into <c>factory.task_dependency</c> for the task this issue
    /// produced. A no-op when this issue has not yet produced a task (retried automatically on a later sync pass
    /// once it does), and each referenced line is likewise skipped — not treated as an error — until its own
    /// referenced issue has produced a task in turn. A reference that would close a dependency cycle is skipped
    /// and surfaced by moving a still-<see cref="FactoryTaskStatus.Pending"/> dependent to
    /// <see cref="FactoryTaskStatus.NeedsHuman"/>, the same visibility <see cref="BlockDependentsOnFailedPrerequisitesAsync"/>-style
    /// issue-derived problems already get; a dependent that has since moved on is left alone (the warning log is
    /// this case's only trace, which is acceptable since it is no longer blocking anything).</summary>
    private async Task ReconcileIssueDependenciesAsync(GitHubRepository repository, GitHubIssue issue, CancellationToken cancellationToken)
    {
        var dependentTaskId = await tasks.FindTaskIdForIssueAsync(repository.Owner, repository.Name, issue.IssueNumber, cancellationToken);
        if (dependentTaskId is null) return;

        var resolvedIds = new List<Guid>();
        foreach (var reference in IssueDependencyParser.Parse(issue.Body))
        {
            var owner = reference.Owner ?? repository.Owner;
            var name = reference.Name ?? repository.Name;
            var resolved = await tasks.FindTaskIdForIssueAsync(owner, name, reference.IssueNumber, cancellationToken);
            if (resolved is not null) resolvedIds.Add(resolved.Value);
            // else: referenced issue/repository has no task yet — retried automatically on a later sync pass.
        }

        var result = await tasks.ReconcileIssueDependenciesAsync(dependentTaskId.Value, resolvedIds, cancellationToken);
        if (result.SkippedCycles.Count == 0) return;

        logger.LogWarning("Task {TaskId} (issue #{IssueNumber}) has {Count} issue-declared dependency edge(s) skipped because they would create a cycle",
            dependentTaskId, issue.IssueNumber, result.SkippedCycles.Count);
        try
        {
            await tasks.TransitionAsync(dependentTaskId.Value, FactoryTaskStatus.Pending, FactoryTaskStatus.NeedsHuman,
                "Blocked: issue body declares a dependency that would create a cycle.", cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Not currently Pending (already progressed, or already resting elsewhere) — the warning log above is
            // this case's trace; no dependent is actually left silently stuck by a cycle it can no longer create.
        }
    }

    /// <summary>Reads and acts on a repository's own <c>TASKS.md</c> tracker file (SF-707), if it has one — the
    /// native, GitHub-issue-free task source for a repository whose backlog lives in a hand-written tracker file.
    /// A no-op for a repository with no <c>TASKS.md</c> at its base branch's root (<see cref="ITrackerFileSync.ReadAsync"/>
    /// returns <see langword="null"/>), and safe to run every cycle alongside GitHub issue sync for a repository
    /// that uses both sources — every tracker-sourced task carries its own distinct <c>tracker_item_id</c>, never
    /// confused with a <c>github_issue_id</c>-carrying task, so neither source can double-claim the other's work.
    /// Three independent passes: (1) create a task for every unchecked "Next up" item not already tracked, (2)
    /// reconcile each item's own <c>Dependencies:</c> line into <c>source='tracker'</c> edges, exactly mirroring
    /// <see cref="ReconcileIssueDependenciesAsync"/>, and (3) write back to the file whichever section
    /// (<see cref="TrackerSectionMapper.From"/>) each already-tracked task's current status now belongs in, if
    /// that differs from what the file was last confirmed to reflect.</summary>
    private async Task SyncTrackerFileAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        string? content;
        try { content = await trackerFileSync.ReadAsync(repository, repository.DefaultBranch, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading TASKS.md failed for {Repository}", $"{repository.Owner}/{repository.Name}");
            return;
        }
        if (content is null) return;

        var items = TasksMdParser.Parse(content);
        var tracked = await tasks.GetTrackerFileTasksAsync(repository.Id, cancellationToken);
        var trackedIds = tracked.Select(t => t.TrackerItemId).ToHashSet();

        var createdCount = 0;
        foreach (var item in items.Where(i => i.Section == TrackerSection.NextUp && !i.Checked && !trackedIds.Contains(i.Id)))
        {
            using var activity = FactoryTelemetry.Source.StartActivity("github.create_task_for_tracker_item");
            activity?.SetTag("factory.repository_id", repository.Id);
            activity?.SetTag("factory.tracker_item_id", item.Id);
            if (await tasks.CreateForTrackerItemIfEligibleAsync(repository.Id, repository.DefaultBranch, item.Id, item.Title, item.Description, cancellationToken))
                createdCount++;
        }
        if (createdCount > 0)
            logger.LogInformation("Synchronized TASKS.md for {Repository}; created {TaskCount} tracker-file tasks", $"{repository.Owner}/{repository.Name}", createdCount);

        foreach (var item in items)
            await ReconcileTrackerDependenciesAsync(repository, item, cancellationToken);

        await WritebackTrackerTransitionsAsync(repository, cancellationToken);
    }

    /// <summary>Parses <paramref name="item"/>'s own <c>Dependencies:</c> line (SF-707) and reconciles the result
    /// into <c>source='tracker'</c> task_dependency edges for the task it produced — the TASKS.md analogue of
    /// <see cref="ReconcileIssueDependenciesAsync"/>. A no-op when this item has not yet produced a task, and each
    /// referenced id is likewise skipped, not treated as an error, until its own item has produced a task in turn
    /// (retried automatically on a later sync pass). Dependencies are resolved within this repository only —
    /// TASKS.md's own <c>Dependencies:</c> convention has no cross-repository reference form, unlike SF-710's
    /// issue-body <c>owner/repo#N</c>.</summary>
    private async Task ReconcileTrackerDependenciesAsync(GitHubRepository repository, TrackerItem item, CancellationToken cancellationToken)
    {
        var dependentTaskId = await tasks.FindTaskIdForTrackerItemAsync(repository.Id, item.Id, cancellationToken);
        if (dependentTaskId is null) return;

        var resolvedIds = new List<Guid>();
        foreach (var dependencyId in item.DependencyIds)
        {
            var resolved = await tasks.FindTaskIdForTrackerItemAsync(repository.Id, dependencyId, cancellationToken);
            if (resolved is not null) resolvedIds.Add(resolved.Value);
        }

        var result = await tasks.ReconcileTrackerDependenciesAsync(dependentTaskId.Value, resolvedIds, cancellationToken);
        if (result.SkippedCycles.Count == 0) return;

        logger.LogWarning("Task {TaskId} (tracker item {TrackerItemId}) has {Count} tracker-declared dependency edge(s) skipped because they would create a cycle",
            dependentTaskId, item.Id, result.SkippedCycles.Count);
        try
        {
            await tasks.TransitionAsync(dependentTaskId.Value, FactoryTaskStatus.Pending, FactoryTaskStatus.NeedsHuman,
                "Blocked: TASKS.md declares a dependency that would create a cycle.", cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Not currently Pending (already progressed, or already resting elsewhere) — the warning log above is
            // this case's trace; no dependent is actually left silently stuck by a cycle it can no longer create.
        }
    }

    /// <summary>Writes back to TASKS.md whichever section (<see cref="TrackerSectionMapper.From"/>) each
    /// tracker-sourced task's current status now belongs in, for every task whose status has moved it into a
    /// different section than the file was last confirmed to reflect (SF-707) — the same role
    /// <c>TaskGitHubNotifier</c> plays for a GitHub issue's own labels/comments, except this is the one component
    /// that owns writing to TASKS.md at all, so a claim (Orchestrator process) and a completion (this process,
    /// below) can never race each other into the same file. A rejected push (see <see cref="ITrackerFileSync.ApplyTransitionAsync"/>)
    /// leaves <c>tracker_writeback_section</c> unchanged, so the next cycle simply retries from the file's
    /// current state instead of losing the update.</summary>
    private async Task WritebackTrackerTransitionsAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        foreach (var task in await tasks.GetTrackerFileTasksAsync(repository.Id, cancellationToken))
        {
            var target = TrackerSectionMapper.From(task.Status);
            if (target == task.WritebackSection) continue;

            var note = target switch
            {
                TrackerSection.Completed => $"Completed {clock.UtcNow:yyyy-MM-dd} by Software Factory; see task {task.TaskId} in the dashboard for full verification evidence.",
                TrackerSection.Blocked => $"Blocked automatically by Software Factory: {task.FailureReason ?? "see task " + task.TaskId + " in the dashboard for details."}",
                _ => null
            };

            using var activity = FactoryTelemetry.Source.StartActivity("github.tracker_file_writeback");
            activity?.SetTag("factory.task_id", task.TaskId);
            activity?.SetTag("factory.tracker_item_id", task.TrackerItemId);
            try
            {
                if (await trackerFileSync.ApplyTransitionAsync(repository, repository.DefaultBranch, task.TrackerItemId, target, note, cancellationToken))
                    await tasks.SetTrackerWritebackSectionAsync(task.TaskId, target, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Writing a TASKS.md update for tracker item {TrackerItemId} failed for {Repository}", task.TrackerItemId, $"{repository.Owner}/{repository.Name}");
            }
        }
    }

    /// <summary>Resolves each task resting in <see cref="FactoryTaskStatus.Published"/> to <see cref="FactoryTaskStatus.Completed"/>
    /// once its pull request is merged, or <see cref="FactoryTaskStatus.Rejected"/> once it is closed without merge.
    /// A pull request that is still open has its CI status synchronized (SF-614), which may in turn trigger an
    /// automatic merge (SF-709) or an automatic CI repair attempt (SF-706).</summary>
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
        if (overallStatus == PullRequestCiStatus.Failure)
            await TryRepairCiFailureAsync(published, result, cancellationToken);
        return overallStatus;
    }

    /// <summary>Reacts to a CI failure just observed on a published pull request's exact current head commit
    /// (SF-706) — <paramref name="result"/> was fetched together with that head in this exact sync pass (SF-614),
    /// so acting on it here can never be stale the way reading a previously-persisted row back out later could
    /// be. A failure <see cref="CiFailureClassifier"/> calls <see cref="ValidationFailureKind.Operational"/> (an
    /// infrastructure/authentication problem CI itself reported, never a code failure) moves the task straight to
    /// <see cref="FactoryTaskStatus.NeedsHuman"/> instead of repeatedly re-running the agent against something no
    /// code change can fix. A repairable failure triggers one bounded automatic continuation via <see cref="ITaskStore.TriggerCiRepairAsync"/> —
    /// but only once per distinct head commit (guarded by <see cref="TaskCiStatus.RepairTriggeredForCommit"/>,
    /// which that same call refreshes) and only up to <see cref="GitHubSyncOptions.MaxCiRepairAttempts"/> total
    /// automatic attempts per task, counted from its own <c>ci-repair</c>-attributed <c>task_feedback</c> rows;
    /// once that bound is reached the task also moves to <see cref="FactoryTaskStatus.NeedsHuman"/>, rather than
    /// looping forever across an unbounded sequence of new failing commits.</summary>
    private async Task TryRepairCiFailureAsync(PublishedTaskRef published, PullRequestChecksResult result, CancellationToken cancellationToken)
    {
        if (result.HeadSha is not { Length: > 0 } headCommit) return;
        var status = await tasks.GetCiStatusAsync(published.TaskId, cancellationToken);
        if (status?.RepairTriggeredForCommit == headCommit) return;

        using var activity = FactoryTelemetry.Source.StartActivity("github.ci_repair");
        activity?.SetTag("factory.task_id", published.TaskId);

        if (CiFailureClassifier.Classify(result) == ValidationFailureKind.Operational)
        {
            await TransitionToNeedsHumanIfStillPublished(published.TaskId,
                $"CI failure looks infrastructure/authentication-related, not a code problem: {DescribeFailures(result)}", cancellationToken);
            return;
        }

        var priorAttempts = (await tasks.GetFeedbackAsync(published.TaskId, cancellationToken)).Count(f => f.CreatedBy == "ci-repair");
        if (priorAttempts >= options.Value.MaxCiRepairAttempts)
        {
            await TransitionToNeedsHumanIfStillPublished(published.TaskId,
                $"Automatic CI repair attempts exhausted ({priorAttempts} of {options.Value.MaxCiRepairAttempts}).", cancellationToken);
            return;
        }

        var feedback = $"CI failed on commit {headCommit}: {DescribeFailures(result)}";
        if (await tasks.TriggerCiRepairAsync(published.TaskId, headCommit, feedback, cancellationToken))
            logger.LogInformation("Triggered automatic CI repair for task {TaskId} on commit {Commit} (attempt {Attempt} of {Max})",
                published.TaskId, headCommit, priorAttempts + 1, options.Value.MaxCiRepairAttempts);
    }

    private static string DescribeFailures(PullRequestChecksResult result) => string.Join("; ",
        result.Checks.Where(c => c.Conclusion == PullRequestCiStatus.Failure).Select(c => c.Url is { Length: > 0 } url ? $"{c.Name} ({url})" : c.Name));

    private async Task TransitionToNeedsHumanIfStillPublished(Guid taskId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await tasks.TransitionAsync(taskId, FactoryTaskStatus.Published, FactoryTaskStatus.NeedsHuman, reason, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Already left Published through some other authoritative action between this poll's sync and here.
        }
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
