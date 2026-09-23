using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.IntegrationTests;

public sealed class PostgresStoreIntegrationTests
{
    [Fact]
    public async Task Active_worker_renews_its_lease_and_cannot_be_reclaimed()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);
            var originalLease = claimed!.LeaseUntil;
            Assert.NotNull(originalLease);

            Assert.True(await fixture.Tasks.RenewLeaseAsync(fixture.TaskId, "worker-a", TimeSpan.FromMinutes(10), CancellationToken.None));
            Assert.False(await fixture.Tasks.RenewLeaseAsync(fixture.TaskId, "worker-b", TimeSpan.FromMinutes(10), CancellationToken.None));
            var renewedLease = await fixture.Connection.ExecuteScalarAsync<DateTime>("SELECT lease_until FROM factory.task WHERE id=@taskId", new { fixture.TaskId });

            Assert.True(DateTime.SpecifyKind(renewedLease, DateTimeKind.Utc) > originalLease.Value.UtcDateTime);
            Assert.Null(await fixture.Tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(2), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Expired_active_execution_is_closed_and_reclaimed_by_another_worker()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);
            var runId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            await fixture.Connection.ExecuteAsync("""
                UPDATE factory.task SET status='Implementing',lease_until=now()-interval '1 minute' WHERE id=@taskId;
                INSERT INTO factory.run(id,task_id,started_at,status,worker_id) VALUES(@runId,@taskId,now()-interval '2 minutes','Running','worker-a');
                INSERT INTO factory.step(id,run_id,step_type,status,started_at,attempt) VALUES(@stepId,@runId,'AgentImplementation','Running',now()-interval '2 minutes',1);
                """, new { fixture.TaskId, runId, stepId });

            var recovered = await fixture.Tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(2), CancellationToken.None);
            var execution = await fixture.Connection.QuerySingleAsync<(string RunStatus, string StepStatus, string? Error)>("""
                SELECT r.status AS "RunStatus",s.status AS "StepStatus",s.error
                FROM factory.run r JOIN factory.step s ON s.run_id=r.id WHERE r.id=@runId
                """, new { runId });

            Assert.Equal(fixture.TaskId, recovered?.Id);
            Assert.Equal("worker-b", recovered?.ClaimedBy);
            Assert.Equal("Failed", execution.RunStatus);
            Assert.Equal("Failed", execution.StepStatus);
            Assert.Contains("lease expired", execution.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(await fixture.Tasks.RenewLeaseAsync(fixture.TaskId, "worker-a", TimeSpan.FromMinutes(2), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Interrupted_execution_is_closed_as_cancelled_and_released_for_recovery()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);
            var runId = await fixture.Tasks.StartRunAsync(fixture.TaskId, "worker-a", CancellationToken.None);
            await fixture.Tasks.StartStepAsync(runId, "AgentImplementation", 1, CancellationToken.None);
            await fixture.Tasks.SetRunConfigurationAsync(runId,
                new RepositoryConfiguration("main", [new ValidationCommand("dotnet", ["build"])], [new ValidationCommand("dotnet", ["test"])], 3, 1, true), CancellationToken.None);

            await fixture.Tasks.CloseExecutionAsync(runId, ExecutionStatus.Cancelled, "Worker stopped", CancellationToken.None);
            await fixture.Tasks.ReleaseLeaseAsync(fixture.TaskId, "worker-a", CancellationToken.None);

            var execution = await fixture.Connection.QuerySingleAsync<(string RunStatus, string StepStatus, string? Error, string Configuration)>("""
                SELECT r.status AS "RunStatus",s.status AS "StepStatus",s.error,r.repository_configuration::text AS "Configuration"
                FROM factory.run r JOIN factory.step s ON s.run_id=r.id WHERE r.id=@runId
                """, new { runId });
            Assert.Equal("Cancelled", execution.RunStatus);
            Assert.Equal("Cancelled", execution.StepStatus);
            Assert.Equal("Worker stopped", execution.Error);
            Assert.Contains("maxImplementationAttempts", execution.Configuration);
            Assert.Contains("dotnet build", execution.Configuration);

            var recovered = await fixture.Tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, recovered?.Id);
            Assert.Equal("worker-b", recovered?.ClaimedBy);
            Assert.Equal("Cancelled", await fixture.Connection.ExecuteScalarAsync<string>("SELECT status FROM factory.run WHERE id=@runId", new { runId }));
        }
    }

    [Fact]
    public async Task Claiming_transitioning_and_retrying_a_task_records_task_events()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);

            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, CancellationToken.None));
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Preparing, FactoryTaskStatus.Failed, "Simulated failure", CancellationToken.None);
            Assert.True(await fixture.Tasks.RetryAsync(fixture.TaskId, CancellationToken.None));

            var events = (await fixture.Connection.QueryAsync<(string FromStatus, string ToStatus, string? Reason, string Actor)>("""
                SELECT from_status AS "FromStatus", to_status AS "ToStatus", reason, actor
                FROM factory.task_event WHERE task_id=@TaskId ORDER BY id
                """, new { fixture.TaskId })).ToList();

            Assert.Equal(4, events.Count);
            Assert.Equal("Pending", events[0].FromStatus); Assert.Equal("Claimed", events[0].ToStatus);
            Assert.Equal("Claimed by worker-a", events[0].Reason); Assert.Equal("orchestrator", events[0].Actor);
            Assert.Equal("Claimed", events[1].FromStatus); Assert.Equal("Preparing", events[1].ToStatus);
            Assert.Null(events[1].Reason); Assert.Equal("orchestrator", events[1].Actor);
            Assert.Equal("Preparing", events[2].FromStatus); Assert.Equal("Failed", events[2].ToStatus);
            Assert.Equal("Simulated failure", events[2].Reason); Assert.Equal("orchestrator", events[2].Actor);
            Assert.Equal("Failed", events[3].FromStatus); Assert.Equal("Pending", events[3].ToStatus);
            Assert.Equal("Retried by operator", events[3].Reason); Assert.Equal("human", events[3].Actor);
        }
    }

    [Fact]
    public async Task Recovering_an_abandoned_task_records_the_original_status_and_worker()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);
            await fixture.Connection.ExecuteAsync("UPDATE factory.task SET status='Implementing', lease_until=now()-interval '1 minute' WHERE id=@TaskId", new { fixture.TaskId });

            var recovered = await fixture.Tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, recovered?.Id);
            Assert.Equal("worker-b", recovered?.ClaimedBy);

            var reason = await fixture.Connection.ExecuteScalarAsync<string>("""
                SELECT reason FROM factory.task_event WHERE task_id=@TaskId AND from_status='Implementing' AND to_status='Claimed'
                """, new { fixture.TaskId });
            Assert.Equal("Recovered from expired lease and claimed by worker-b", reason);
        }
    }

    [Fact]
    public async Task Reaching_a_resting_state_releases_ownership_so_an_expired_lease_can_never_reclaim_it()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);

            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, CancellationToken.None);
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null, CancellationToken.None);
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null, CancellationToken.None);
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Validating, FactoryTaskStatus.ReadyForPublish, null, CancellationToken.None);

            var (status, claimedBy, leaseUntil) = await fixture.Connection.QuerySingleAsync<(string Status, string? ClaimedBy, DateTime? LeaseUntil)>(
                "SELECT status,claimed_by AS \"ClaimedBy\",lease_until AS \"LeaseUntil\" FROM factory.task WHERE id=@TaskId", new { fixture.TaskId });
            Assert.Equal("ReadyForPublish", status);
            Assert.Null(claimedBy);
            Assert.Null(leaseUntil);

            // SF-601 regression: even a stale lease timestamp left over from the worker's last renewal before the
            // task came to rest (long since passed) must never make a validated, awaiting-publication task look
            // like an abandoned execution.
            await fixture.Connection.ExecuteAsync("UPDATE factory.task SET lease_until=now()-interval '1 hour' WHERE id=@TaskId", new { fixture.TaskId });

            var reclaimed = await fixture.Tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Null(reclaimed);
            Assert.Equal("ReadyForPublish", await fixture.Connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));
        }
    }

    [Fact]
    public async Task Executing_statuses_keep_ownership_but_a_resting_status_releases_it()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(10), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);

            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, CancellationToken.None);
            var stillOwned = await fixture.Connection.QuerySingleAsync<(string? ClaimedBy, DateTime? LeaseUntil)>(
                "SELECT claimed_by AS \"ClaimedBy\",lease_until AS \"LeaseUntil\" FROM factory.task WHERE id=@TaskId", new { fixture.TaskId });
            Assert.Equal("worker-a", stillOwned.ClaimedBy);
            Assert.NotNull(stillOwned.LeaseUntil);

            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null, CancellationToken.None);
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Implementing, FactoryTaskStatus.WaitingForQuota, "Codex quota reached", CancellationToken.None);

            var released = await fixture.Connection.QuerySingleAsync<(string? ClaimedBy, DateTime? ClaimedAt, DateTime? LeaseUntil)>(
                "SELECT claimed_by AS \"ClaimedBy\",claimed_at AS \"ClaimedAt\",lease_until AS \"LeaseUntil\" FROM factory.task WHERE id=@TaskId", new { fixture.TaskId });
            Assert.Null(released.ClaimedBy);
            Assert.Null(released.ClaimedAt);
            Assert.Null(released.LeaseUntil);
        }
    }

    [Fact]
    public async Task Publication_remains_possible_once_a_validated_tasks_ownership_is_released()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.task SET branch_name='factory/1-x', worktree_path='/tmp/wt/ready-for-publish' WHERE id=@TaskId", new { fixture.TaskId });

                var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(10), CancellationToken.None);
                Assert.Equal(fixture.TaskId, claimed?.Id);
                await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, CancellationToken.None);
                await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null, CancellationToken.None);
                await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null, CancellationToken.None);
                await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Validating, FactoryTaskStatus.ReadyForPublish, null, CancellationToken.None);
                Assert.Null(await fixture.Connection.ExecuteScalarAsync<string?>("SELECT claimed_by FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));

                var requested = await fixture.Tasks.RequestPublicationAsync(fixture.TaskId, null, "operator", CancellationToken.None);
                Assert.NotNull(requested);
                var publication = await fixture.Tasks.ClaimNextPublicationAsync("publication-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
                Assert.NotNull(publication);
                Assert.Equal(fixture.TaskId, publication!.TaskId);
                Assert.Equal("factory/1-x", publication.BranchName);

                await fixture.Tasks.CompletePublicationAsync(publication.Id, "PullRequestCreated", 7, "https://github.com/lease-tests/repo/pull/7", null, CancellationToken.None);
                await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published, null, CancellationToken.None);
                Assert.Equal("Published", await fixture.Connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@TaskId", new { fixture.TaskId });
            }
        }
    }

    [Fact]
    public async Task Change_summary_is_persisted_on_the_run()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);
            var runId = await fixture.Tasks.StartRunAsync(fixture.TaskId, "worker-a", CancellationToken.None);
            var summary = new ChangeSummary(true, "factory/142-add-export", "abc1234", "def5678", ["src/Export.cs", "tests/ExportTests.cs"], 42, 7);

            await fixture.Tasks.SetChangeSummaryAsync(runId, summary, CancellationToken.None);

            var persisted = await fixture.Connection.QuerySingleAsync<(string BaseCommit, string HeadCommit, string[] FilesChanged, int LinesAdded, int LinesRemoved)>("""
                SELECT base_commit AS "BaseCommit", head_commit AS "HeadCommit", files_changed AS "FilesChanged", lines_added AS "LinesAdded", lines_removed AS "LinesRemoved"
                FROM factory.run WHERE id=@runId
                """, new { runId });
            Assert.Equal("abc1234", persisted.BaseCommit);
            Assert.Equal("def5678", persisted.HeadCommit);
            Assert.Equal(new[] { "src/Export.cs", "tests/ExportTests.cs" }, persisted.FilesChanged);
            Assert.Equal(42, persisted.LinesAdded);
            Assert.Equal(7, persisted.LinesRemoved);
        }
    }

    [Fact]
    public async Task Publication_request_is_claimed_and_completed_and_completes_the_task()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('publication-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        await connection.ExecuteAsync("""
            INSERT INTO factory.task(id,repository_id,title,status,base_branch,branch_name,worktree_path,priority)
            VALUES(@taskId,@repositoryId,'Publication integration task','ReadyForPublish','main','factory/1-x','/tmp/wt/1',0)
            """, new { taskId, repositoryId });
        try
        {
            var requested = await tasks.RequestPublicationAsync(taskId, null, "operator", CancellationToken.None);
            Assert.NotNull(requested);
            Assert.Null(await tasks.RequestPublicationAsync(taskId, null, "operator", CancellationToken.None));

            var claimed = await tasks.ClaimNextPublicationAsync("publication-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(requested, claimed!.Id);
            Assert.Equal(taskId, claimed.TaskId);
            Assert.Equal("factory/1-x", claimed.BranchName);
            Assert.Equal("/tmp/wt/1", claimed.WorktreePath);
            Assert.Equal("main", claimed.BaseBranch);
            Assert.Equal(repositoryId, claimed.RepositoryId);
            Assert.Equal("publication-tests", claimed.RepositoryOwner);
            Assert.Equal(suffix, claimed.RepositoryName);
            Assert.Null(claimed.ValidatedHeadCommit);
            Assert.Null(await tasks.ClaimNextPublicationAsync("another-worker", TimeSpan.FromMinutes(5), CancellationToken.None));

            await tasks.CompletePublicationAsync(claimed.Id, "PullRequestCreated", 42, "https://github.com/publication-tests/repo/pull/42", null, CancellationToken.None);
            await tasks.TransitionAsync(taskId, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published, null, CancellationToken.None);

            var persisted = await connection.QuerySingleAsync<(string Status, int? PullRequestNumber, string? PullRequestUrl)>("""
                SELECT status,pull_request_number AS "PullRequestNumber",pull_request_url AS "PullRequestUrl" FROM factory.publication WHERE id=@id
                """, new { id = claimed.Id });
            Assert.Equal("PullRequestCreated", persisted.Status);
            Assert.Equal(42, persisted.PullRequestNumber);
            Assert.Equal("https://github.com/publication-tests/repo/pull/42", persisted.PullRequestUrl);
            Assert.Equal("Published", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@taskId", new { taskId }));

            // Sync finds the published task by its recorded pull request and resolves it once merged.
            var published = Assert.Single(await tasks.GetPublishedTasksAsync(CancellationToken.None), p => p.TaskId == taskId);
            Assert.Equal("publication-tests", published.RepositoryOwner);
            Assert.Equal(suffix, published.RepositoryName);
            Assert.Equal(42, published.PullRequestNumber);

            await tasks.RecordGitHubWriteAsync(taskId, "comment", "Task started.", true, null, CancellationToken.None);
            await tasks.RecordGitHubWriteAsync(taskId, "label", "factory:in-progress", false, "gh: not found", CancellationToken.None);
            var writes = (await connection.QueryAsync<(string Kind, string Detail, bool Succeeded, string? Error)>(
                "SELECT kind AS \"Kind\",detail AS \"Detail\",succeeded AS \"Succeeded\",error AS \"Error\" FROM factory.github_write WHERE task_id=@taskId ORDER BY created_at", new { taskId })).ToList();
            Assert.Equal(2, writes.Count);
            Assert.Equal(("comment", "Task started.", true, (string?)null), writes[0]);
            Assert.Equal(("label", "factory:in-progress", false, "gh: not found"), writes[1]);

            await tasks.TransitionAsync(taskId, FactoryTaskStatus.Published, FactoryTaskStatus.Completed, null, CancellationToken.None);
            Assert.Equal("Completed", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@taskId", new { taskId }));
            Assert.DoesNotContain(await tasks.GetPublishedTasksAsync(CancellationToken.None), p => p.TaskId == taskId);

            // A completed attempt does not block a fresh request.
            Assert.NotNull(await tasks.RequestPublicationAsync(taskId, null, "operator", CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync(
                "DELETE FROM factory.github_write WHERE task_id=@taskId; DELETE FROM factory.publication WHERE task_id=@taskId; DELETE FROM factory.task WHERE id=@taskId;",
                new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task A_stale_publishing_claim_is_reclaimed_after_its_lease_expires()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.task SET status='ReadyForPublish', branch_name='factory/1-x', worktree_path='/tmp/wt/stale' WHERE id=@TaskId", new { fixture.TaskId });

                var requested = await fixture.Tasks.RequestPublicationAsync(fixture.TaskId, null, "operator", CancellationToken.None);
                Assert.NotNull(requested);

                // A worker claimed it and is still within its lease.
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.publication SET status='Publishing',claimed_by='live-worker',claimed_at=now(),lease_until=now()+interval '5 minutes' WHERE id=@id",
                    new { id = requested });

                // A live worker with time left on its own claim is never reclaimed out from under it.
                Assert.Null(await fixture.Tasks.ClaimNextPublicationAsync("worker-b", TimeSpan.FromMinutes(5), CancellationToken.None));

                await fixture.Connection.ExecuteAsync("UPDATE factory.publication SET lease_until=now()-interval '1 second' WHERE id=@id", new { id = requested });
                var reclaimed = await fixture.Tasks.ClaimNextPublicationAsync("worker-b", TimeSpan.FromMinutes(5), CancellationToken.None);
                Assert.NotNull(reclaimed);
                Assert.Equal(requested, reclaimed!.Id);
                Assert.Equal("worker-b", await fixture.Connection.ExecuteScalarAsync<string>("SELECT claimed_by FROM factory.publication WHERE id=@id", new { id = requested }));
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@TaskId", new { fixture.TaskId });
            }
        }
    }

    [Fact]
    public async Task Validated_head_commit_is_persisted_and_returned_with_a_claimed_publication()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.task SET status='ReadyForPublish', branch_name='factory/1-x', worktree_path='/tmp/wt/head' WHERE id=@TaskId", new { fixture.TaskId });

                await fixture.Tasks.SetValidatedHeadCommitAsync(fixture.TaskId, "cafef00d", CancellationToken.None);

                var requested = await fixture.Tasks.RequestPublicationAsync(fixture.TaskId, null, "operator", CancellationToken.None);
                var claimed = await fixture.Tasks.ClaimNextPublicationAsync("publication-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
                Assert.NotNull(claimed);
                Assert.Equal(requested, claimed!.Id);
                Assert.Equal("cafef00d", claimed.ValidatedHeadCommit);
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@TaskId", new { fixture.TaskId });
            }
        }
    }

    [Fact]
    public async Task Require_human_merge_defaults_true_and_is_persisted_and_returned_with_a_claimed_publication()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.task SET status='ReadyForPublish', branch_name='factory/1-x', worktree_path='/tmp/wt/rhm' WHERE id=@TaskId", new { fixture.TaskId });

                Assert.True(await fixture.Connection.ExecuteScalarAsync<bool>("SELECT require_human_merge FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));

                await fixture.Tasks.SetRequireHumanMergeAsync(fixture.TaskId, false, CancellationToken.None);

                var requested = await fixture.Tasks.RequestPublicationAsync(fixture.TaskId, null, "operator", CancellationToken.None);
                var claimed = await fixture.Tasks.ClaimNextPublicationAsync("publication-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
                Assert.NotNull(claimed);
                Assert.Equal(requested, claimed!.Id);
                Assert.False(claimed.RequireHumanMerge);
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@TaskId", new { fixture.TaskId });
            }
        }
    }

    [Fact]
    public async Task Published_tasks_report_their_own_effective_merge_policy()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.task SET status='ReadyForPublish', branch_name='factory/1-x', worktree_path='/tmp/wt/rhm-published' WHERE id=@TaskId", new { fixture.TaskId });
                await fixture.Tasks.SetRequireHumanMergeAsync(fixture.TaskId, false, CancellationToken.None);

                var requested = await fixture.Tasks.RequestPublicationAsync(fixture.TaskId, null, "operator", CancellationToken.None);
                var claimed = await fixture.Tasks.ClaimNextPublicationAsync("publication-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
                Assert.NotNull(claimed);
                await fixture.Tasks.CompletePublicationAsync(claimed!.Id, "PullRequestCreated", 77, "https://github.com/lease-tests/repo/pull/77", null, CancellationToken.None);
                await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published, null, CancellationToken.None);

                var published = Assert.Single(await fixture.Tasks.GetPublishedTasksAsync(CancellationToken.None), p => p.TaskId == fixture.TaskId);
                Assert.False(published.RequireHumanMerge);
                Assert.Equal(requested, claimed.Id);
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@TaskId", new { fixture.TaskId });
            }
        }
    }

    [Fact]
    public async Task Reconciliation_completes_the_task_transition_when_the_pull_request_already_exists()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.task SET status='ReadyForPublish', branch_name='factory/1-x', worktree_path='/tmp/wt/reconcile' WHERE id=@TaskId", new { fixture.TaskId });

                // Simulates the crash between recording a successful publication and completing the task's own
                // transition: the publication is already done, but the task is still resting in ReadyForPublish.
                var requested = await fixture.Tasks.RequestPublicationAsync(fixture.TaskId, null, "operator", CancellationToken.None);
                var claimed = await fixture.Tasks.ClaimNextPublicationAsync("publication-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
                Assert.NotNull(claimed);
                await fixture.Tasks.CompletePublicationAsync(claimed!.Id, "PullRequestCreated", 55, "https://github.com/lease-tests/repo/pull/55", null, CancellationToken.None);
                Assert.Equal("ReadyForPublish", await fixture.Connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));

                var reconciled = await fixture.Tasks.ReconcilePublishedTasksAsync(CancellationToken.None);

                Assert.True(reconciled >= 1);
                Assert.Equal("Published", await fixture.Connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));
                Assert.Null(await fixture.Connection.ExecuteScalarAsync<string?>("SELECT claimed_by FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));

                // Reconciling again is a no-op: the task is no longer ReadyForPublish, so it is not matched again.
                Assert.Equal(0, await fixture.Tasks.ReconcilePublishedTasksAsync(CancellationToken.None));
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@TaskId", new { fixture.TaskId });
            }
        }
    }

    [Fact]
    public async Task Reconciliation_leaves_a_task_untouched_while_its_publication_is_still_in_flight()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                await fixture.Connection.ExecuteAsync(
                    "UPDATE factory.task SET status='ReadyForPublish', branch_name='factory/1-x', worktree_path='/tmp/wt/inflight' WHERE id=@TaskId", new { fixture.TaskId });
                await fixture.Tasks.RequestPublicationAsync(fixture.TaskId, null, "operator", CancellationToken.None);

                await fixture.Tasks.ReconcilePublishedTasksAsync(CancellationToken.None);

                Assert.Equal("ReadyForPublish", await fixture.Connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@TaskId", new { fixture.TaskId });
            }
        }
    }

    [Fact]
    public async Task Agent_attribution_prefers_the_currently_invoking_agent_then_the_most_recent_run_then_the_preference()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            await fixture.Connection.ExecuteAsync("UPDATE factory.task SET preferred_agent='Codex' WHERE id=@TaskId", new { fixture.TaskId });

            // No invocation has ever happened yet: falls back to the task's own preference.
            Assert.Equal("Codex", await ResolveAgentAsync(fixture));

            // A fallback run actually executed under a different agent than preferred — the most recent real
            // invocation wins over the stale preference, exactly the "fallback attribution" bug SF-609 fixes.
            var runId = await fixture.Tasks.StartRunAsync(fixture.TaskId, "worker-a", CancellationToken.None);
            var stepId = await fixture.Tasks.StartStepAsync(runId, "AgentImplementation", 1, CancellationToken.None);
            await fixture.Tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), fixture.TaskId, runId, stepId, "Claude",
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4), 60, 0, "Succeeded", "", "", false, null, 1, false, null),
                CancellationToken.None);
            Assert.Equal("Claude", await ResolveAgentAsync(fixture));

            // A second attempt is now actively invoking Codex again: the live in-flight signal takes priority
            // over history, so the task is correctly attributed to whoever is actually running it right now.
            await fixture.Tasks.SetCurrentAgentAsync(fixture.TaskId, "Codex", CancellationToken.None);
            Assert.Equal("Codex", await ResolveAgentAsync(fixture));

            // Any status transition clears the live signal, so a finished invocation never looks permanently busy.
            await fixture.Connection.ExecuteAsync("UPDATE factory.task SET status='Implementing' WHERE id=@TaskId", new { fixture.TaskId });
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Implementing, FactoryTaskStatus.Validating, null, CancellationToken.None);
            Assert.Equal("Claude", await ResolveAgentAsync(fixture));
        }

        static async Task<string?> ResolveAgentAsync(LeaseFixture fixture) =>
            await fixture.Connection.ExecuteScalarAsync<string?>("""
                SELECT COALESCE(t.current_agent,(SELECT ar.agent FROM factory.agent_run ar WHERE ar.task_id=t.id ORDER BY ar.started_at DESC LIMIT 1),t.preferred_agent,'Codex')
                FROM factory.task t WHERE t.id=@TaskId
                """, new { fixture.TaskId });
    }

    [Fact]
    public async Task Claiming_an_abandoned_execution_clears_a_stale_current_agent()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            var claimed = await fixture.Tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(fixture.TaskId, claimed?.Id);
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, null, CancellationToken.None);
            await fixture.Tasks.TransitionAsync(fixture.TaskId, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing, null, CancellationToken.None);
            await fixture.Tasks.SetCurrentAgentAsync(fixture.TaskId, "Codex", CancellationToken.None);
            // Simulate the worker crashing mid-invocation, exactly like the existing abandoned-execution scenario.
            await fixture.Connection.ExecuteAsync("UPDATE factory.task SET lease_until=now()-interval '1 minute' WHERE id=@TaskId", new { fixture.TaskId });

            var recovered = await fixture.Tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(2), CancellationToken.None);

            Assert.Equal(fixture.TaskId, recovered?.Id);
            Assert.Null(await fixture.Connection.ExecuteScalarAsync<string?>("SELECT current_agent FROM factory.task WHERE id=@TaskId", new { fixture.TaskId }));
        }
    }

    [Fact]
    public async Task Eligible_issue_is_created_once_and_can_be_claimed()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var github = new PostgresGitHubStore(settings);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");
        var repository = new GitHubRepository(0, "factory-tests", suffix, $"https://example.invalid/{suffix}.git", "main", true);
        await github.UpsertRepositoryAsync(repository, CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        var repositoryId = await connection.ExecuteScalarAsync<long>("SELECT id FROM github.repository WHERE owner='factory-tests' AND name=@suffix", new { suffix });
        long issueId = 0;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var issue = await github.UpsertIssueAsync(repositoryId, new GitHubIssue(0, repositoryId, Random.Shared.NextInt64(1, long.MaxValue),
                1, "Integration task", "Body", "OPEN", "tester", now, now, ["factory:ready"],
                [new GitHubComment(Random.Shared.NextInt64(1, long.MaxValue), "reviewer", "Context", now, now)]), CancellationToken.None);
            issueId = issue.Id;

            var loadedRepository = await github.GetRepositoryAsync(repositoryId, CancellationToken.None);
            var loadedIssue = await github.GetIssueAsync(issueId, CancellationToken.None);
            Assert.Equal(suffix, loadedRepository?.Name);
            Assert.Contains("factory:ready", loadedIssue?.Labels ?? []);
            Assert.Equal("Context", Assert.Single(loadedIssue?.Comments ?? []).Body);

            await github.RecordRepositorySyncFailureAsync(repositoryId, "GitHub CLI timed out", CancellationToken.None);
            var syncFailure = await connection.QuerySingleAsync<string>("SELECT error FROM github.repository_sync_failure WHERE repository_id=@repositoryId", new { repositoryId });
            Assert.Equal("GitHub CLI timed out", syncFailure);

            Assert.True(await tasks.CreateForIssueIfEligibleAsync(issue, "main", CancellationToken.None));
            Assert.False(await tasks.CreateForIssueIfEligibleAsync(issue, "main", CancellationToken.None));
            await connection.ExecuteAsync("UPDATE factory.task SET priority=2147483647 WHERE github_issue_id=@issueId", new { issueId });

            var claimed = await tasks.ClaimNextAsync("integration-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(issueId, claimed.GitHubIssueId);
            Assert.Equal(FactoryTaskStatus.Claimed, claimed.Status);

            var runId = await tasks.StartRunAsync(claimed.Id, "integration-worker", CancellationToken.None);
            var stepId = await tasks.StartStepAsync(runId, "AgentImplementation", 1, CancellationToken.None);
            var agentResult = new AgentResult("needs-human", "Review required", ["dotnet test"], true,
                ["src/Feature.cs"], ["Manual rollout"], true, "Approve deployment");
            var agentStartedAt = DateTimeOffset.UtcNow;
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), claimed.Id, runId, stepId, "Codex", agentStartedAt,
                agentStartedAt.AddSeconds(1), 1, 0, "Succeeded", "output", "", false, null, 1, true, agentResult), CancellationToken.None);

            var persisted = await connection.QuerySingleAsync<(string Summary, string Files, string Risks, string HumanReason)>(
                "SELECT result_summary,files_changed::text,risks::text,human_reason FROM factory.agent_run WHERE task_id=@taskId",
                new { taskId = claimed.Id });
            Assert.Equal("Review required", persisted.Summary);
            Assert.Contains("src/Feature.cs", persisted.Files);
            Assert.Contains("Manual rollout", persisted.Risks);
            Assert.Equal("Approve deployment", persisted.HumanReason);
        }
        finally
        {
            if (issueId != 0) await connection.ExecuteAsync("""
                DELETE FROM factory.agent_run WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=@issueId);
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=@issueId));
                DELETE FROM factory.run WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=@issueId);
                DELETE FROM factory.task WHERE github_issue_id=@issueId;
                DELETE FROM github.issue WHERE id=@issueId;
                """, new { issueId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Sync_checkpoint_closed_at_and_pending_cancellation_are_persisted()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var github = new PostgresGitHubStore(settings);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");
        var repository = new GitHubRepository(0, "factory-tests", suffix, $"https://example.invalid/{suffix}.git", "main", true);
        await github.UpsertRepositoryAsync(repository, CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        var repositoryId = await connection.ExecuteScalarAsync<long>("SELECT id FROM github.repository WHERE owner='factory-tests' AND name=@suffix", new { suffix });
        long issueId = 0;
        try
        {
            var enabled = await github.GetEnabledRepositoriesAsync(CancellationToken.None);
            Assert.Null(enabled.Single(r => r.Id == repositoryId).LastSyncedAt);

            var checkpoint = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
            await github.MarkRepositorySyncedAsync(repositoryId, checkpoint, CancellationToken.None);
            var afterMark = await github.GetRepositoryAsync(repositoryId, CancellationToken.None);
            Assert.Equal(checkpoint, afterMark?.LastSyncedAt);

            // PostgreSQL's timestamptz only stores microsecond precision, so a value derived from UtcNow (which
            // carries 100ns ticks) needs truncating before an exact round-trip equality check below.
            var now = TruncateToMicroseconds(DateTimeOffset.UtcNow);
            var closedAt = now.AddMinutes(-1);
            var issue = await github.UpsertIssueAsync(repositoryId, new GitHubIssue(0, repositoryId, Random.Shared.NextInt64(1, long.MaxValue),
                1, "Closed issue", "Body", "CLOSED", "tester", now, now, ["factory:ready"], [], closedAt), CancellationToken.None);
            issueId = issue.Id;

            var reloaded = await github.GetIssueAsync(issueId, CancellationToken.None);
            Assert.Equal(closedAt, reloaded?.ClosedAt);

            // A closed issue is not eligible, so no task should ever have been created for it, and cancelling is a no-op.
            Assert.False(await tasks.CancelPendingForIssueAsync(issueId, "Issue was closed on GitHub.", CancellationToken.None));

            var openIssue = issue with { State = "OPEN", ClosedAt = null };
            Assert.True(await tasks.CreateForIssueIfEligibleAsync(openIssue, "main", CancellationToken.None));
            var status = await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE github_issue_id=@issueId", new { issueId });
            Assert.Equal("Pending", status);

            Assert.True(await tasks.CancelPendingForIssueAsync(issueId, "Issue was closed on GitHub.", CancellationToken.None));
            var (cancelledStatus, reason) = await connection.QuerySingleAsync<(string Status, string? Reason)>(
                "SELECT status,failure_reason FROM factory.task WHERE github_issue_id=@issueId", new { issueId });
            Assert.Equal("Cancelled", cancelledStatus);
            var eventReason = await connection.QuerySingleAsync<string>(
                "SELECT reason FROM factory.task_event WHERE task_id=(SELECT id FROM factory.task WHERE github_issue_id=@issueId) AND to_status='Cancelled'", new { issueId });
            Assert.Equal("Issue was closed on GitHub.", eventReason);

            // Cancelling again is a no-op: the task is no longer Pending.
            Assert.False(await tasks.CancelPendingForIssueAsync(issueId, "Issue was closed on GitHub.", CancellationToken.None));
        }
        finally
        {
            if (issueId != 0) await connection.ExecuteAsync("""
                DELETE FROM factory.task_event WHERE task_id IN (SELECT id FROM factory.task WHERE github_issue_id=@issueId);
                DELETE FROM factory.task WHERE github_issue_id=@issueId;
                DELETE FROM github.issue WHERE id=@issueId;
                """, new { issueId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, value.Offset);

    [Fact]
    public async Task Attempt_count_previous_summary_and_expired_quota_resume_are_persisted()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('attempt-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        var quotaTaskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch)
                VALUES(@taskId,@repositoryId,'Attempt tracking task','Implementing','main')
                """, new { taskId, repositoryId });

            Assert.Equal(0, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));
            Assert.Null(await tasks.GetPreviousAttemptAsync(taskId, CancellationToken.None));

            var runId = await tasks.StartRunAsync(taskId, "integration-worker", CancellationToken.None);
            var stepId = await tasks.StartStepAsync(runId, "AgentImplementation", 1, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), taskId, runId, stepId, "Codex", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, 1, 0, "Succeeded", "out", "", false, null, 1, false,
                new AgentResult("completed", "Implemented the wrong endpoint", ["dotnet test"], true, ["src/Export.cs"], [], false, null)), CancellationToken.None);
            var buildStepId = await tasks.StartStepAsync(runId, "Build", 1, CancellationToken.None);
            await tasks.CompleteStepAsync(buildStepId, ExecutionStatus.Failed, "compile error", "error CS0103", CancellationToken.None);

            Assert.Equal(1, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));
            var previous = await tasks.GetPreviousAttemptAsync(taskId, CancellationToken.None);
            Assert.NotNull(previous);
            Assert.Equal("Implemented the wrong endpoint", previous!.AgentSummary);
            Assert.Contains("compile error", previous.ValidationOutput);
            Assert.Contains("src/Export.cs", previous.ChangedFiles);

            // Quota status (SF-602) is persisted independently of this agent_run audit row: IsAgentAtQuotaAsync
            // never derives from agent_run history, only from a dedicated RecordAgentQuotaStatusAsync call.
            Assert.False(await tasks.IsAgentAtQuotaAsync("NeverUsedAgent", CancellationToken.None));
            var quotaCheckStepId = await tasks.StartStepAsync(runId, "AgentImplementation", 2, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), taskId, runId, quotaCheckStepId, "Claude", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, 1, 1, "Failed", "", "rate limited", true, DateTimeOffset.UtcNow.AddHours(4), 2, false, null,
                CountsAsImplementationAttempt: false), CancellationToken.None);
            Assert.False(await tasks.IsAgentAtQuotaAsync("Claude", CancellationToken.None));

            var checkedAt = DateTimeOffset.UtcNow;
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Claude", true, QuotaWindow.ShortTerm, QuotaResetKind.Estimated,
                checkedAt.AddHours(4), checkedAt, "rate limited"), CancellationToken.None);
            Assert.True(await tasks.IsAgentAtQuotaAsync("Claude", CancellationToken.None));

            // Restart persistence: a fresh store instance (simulating a process restart) reads the same status.
            var restarted = new PostgresTaskStore(settings, new TestClock());
            var status = await restarted.GetAgentQuotaStatusAsync("Claude", CancellationToken.None);
            Assert.NotNull(status);
            Assert.True(status!.Detected);
            Assert.Equal(QuotaWindow.ShortTerm, status.Window);
            Assert.Equal(QuotaResetKind.Estimated, status.ResetKind);
            Assert.Equal("rate limited", status.Detail);
            Assert.True(await restarted.IsAgentAtQuotaAsync("Claude", CancellationToken.None));

            // A later invocation that does not detect quota clears the agent's status immediately, without
            // waiting for the previously recorded reset time to pass.
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Claude", false, QuotaWindow.None, QuotaResetKind.None,
                null, checkedAt.AddMinutes(1), null), CancellationToken.None);
            Assert.False(await tasks.IsAgentAtQuotaAsync("Claude", CancellationToken.None));

            // ClearAgentQuotaAsync is the operator override for a stale quota status: it works even while an
            // agent is currently detected at quota, without waiting for a later invocation to overwrite it.
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Claude", true, QuotaWindow.ShortTerm, QuotaResetKind.Estimated,
                checkedAt.AddHours(4), checkedAt, "rate limited"), CancellationToken.None);
            Assert.True(await tasks.IsAgentAtQuotaAsync("Claude", CancellationToken.None));
            Assert.True(await tasks.ClearAgentQuotaAsync("Claude", CancellationToken.None));
            Assert.False(await tasks.IsAgentAtQuotaAsync("Claude", CancellationToken.None));
            var clearedStatus = await tasks.GetAgentQuotaStatusAsync("Claude", CancellationToken.None);
            Assert.NotNull(clearedStatus);
            Assert.False(clearedStatus!.Detected);
            Assert.Null(clearedStatus.ResetAt);

            // An agent with no recorded quota status at all has nothing to clear.
            Assert.False(await tasks.ClearAgentQuotaAsync("NeverUsedAgent", CancellationToken.None));

            // SF-604 regression: a task that goes straight to WaitingForQuota with no prior invocation at all
            // (every configured agent was already at quota on its very first attempt) still resumes once a
            // configured provider becomes available — resumption is scheduled from provider availability, never
            // the waiting task's own invocation history (which here does not exist).
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@quotaTaskId,@repositoryId,'Quota task','WaitingForQuota','main')
                """, new { quotaTaskId, repositoryId });
            Assert.Equal(0, await tasks.CountAgentRunsAsync(quotaTaskId, CancellationToken.None));

            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Codex", true, QuotaWindow.ShortTerm, QuotaResetKind.Estimated,
                DateTimeOffset.UtcNow.AddHours(4), DateTimeOffset.UtcNow, "quota"), CancellationToken.None);
            Assert.Equal(0, await tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None));
            Assert.Equal("WaitingForQuota", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@quotaTaskId", new { quotaTaskId }));

            // Codex's reset time passing makes it available again.
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Codex", true, QuotaWindow.ShortTerm, QuotaResetKind.Estimated,
                DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, "quota"), CancellationToken.None);
            Assert.Equal(1, await tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None));
            Assert.Equal("Pending", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@quotaTaskId", new { quotaTaskId }));
            Assert.Equal(0, await tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.agent_availability WHERE agent IN ('Claude','Codex')");
            await connection.ExecuteAsync("""
                DELETE FROM factory.agent_run WHERE task_id IN (@taskId,@quotaTaskId);
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id IN (@taskId,@quotaTaskId));
                DELETE FROM factory.run WHERE task_id IN (@taskId,@quotaTaskId);
                DELETE FROM factory.task WHERE id IN (@taskId,@quotaTaskId);
                """, new { taskId, quotaTaskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task A_task_waits_out_two_blocked_providers_and_resumes_the_moment_either_becomes_available()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('two-provider-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@taskId,@repositoryId,'Two-provider task','WaitingForQuota','main')
                """, new { taskId, repositoryId });

            var now = DateTimeOffset.UtcNow;
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Codex", true, QuotaWindow.ShortTerm, QuotaResetKind.Estimated, now.AddHours(5), now, "quota"), CancellationToken.None);
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Claude", true, QuotaWindow.ShortTerm, QuotaResetKind.Estimated, now.AddHours(5), now, "rate limited"), CancellationToken.None);

            // Both configured providers are blocked: nothing resumes.
            Assert.Equal(0, await tasks.ResumeExpiredQuotaTasksAsync(["Codex", "Claude"], CancellationToken.None));
            Assert.Equal("WaitingForQuota", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@taskId", new { taskId }));

            // Claude becomes available; the task resumes even though Codex (its history-adjacent provider) is still blocked.
            await tasks.RecordAgentQuotaStatusAsync(new AgentQuotaStatus("Claude", false, QuotaWindow.None, QuotaResetKind.None, null, now, null), CancellationToken.None);
            Assert.Equal(1, await tasks.ResumeExpiredQuotaTasksAsync(["Codex", "Claude"], CancellationToken.None));
            Assert.Equal("Pending", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@taskId", new { taskId }));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.agent_availability WHERE agent IN ('Codex','Claude')");
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id=@taskId", new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Concurrent_resume_calls_never_resume_the_same_waiting_task_twice()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('concurrent-resume-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskAId = Guid.NewGuid();
        var taskBId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch,priority)
                VALUES(@taskAId,@repositoryId,'Concurrent A','WaitingForQuota','main',2),
                      (@taskBId,@repositoryId,'Concurrent B','WaitingForQuota','main',1)
                """, new { taskAId, taskBId, repositoryId });
            // No agent_availability row at all: a never-invoked provider is treated as available, so both
            // tasks are immediately eligible — this exercises SKIP LOCKED actually mattering under concurrency.

            var results = await Task.WhenAll(
                tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None),
                tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None));

            Assert.Equal(2, results.Sum());
            var statuses = (await connection.QueryAsync<string>("SELECT status FROM factory.task WHERE id IN (@taskAId,@taskBId)", new { taskAId, taskBId })).ToList();
            Assert.All(statuses, s => Assert.Equal("Pending", s));
            Assert.Equal(0, await tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@taskAId,@taskBId)", new { taskAId, taskBId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Dispatch_pause_state_is_durable_and_resuming_clears_the_reason()
    {
        var fixture = await LeaseFixture.CreateAsync();
        if (fixture is null) return;
        await using (fixture)
        {
            try
            {
                Assert.False(await fixture.Tasks.IsDispatchPausedAsync(CancellationToken.None));
                Assert.False(await fixture.Tasks.IsAgentPausedAsync("Codex", CancellationToken.None));
                var notPaused = await fixture.Tasks.GetDispatchPauseAsync(DispatchPauseScope.Global, CancellationToken.None);
                Assert.False(notPaused.Paused);
                Assert.Null(notPaused.Reason);

                await fixture.Tasks.SetDispatchPauseAsync(DispatchPauseScope.Global, true, "Reserving capacity for interactive use", "operator", CancellationToken.None);
                await fixture.Tasks.SetDispatchPauseAsync("Codex", true, "Interactive debugging session", "operator", CancellationToken.None);

                Assert.True(await fixture.Tasks.IsDispatchPausedAsync(CancellationToken.None));
                Assert.True(await fixture.Tasks.IsAgentPausedAsync("Codex", CancellationToken.None));
                Assert.False(await fixture.Tasks.IsAgentPausedAsync("Claude", CancellationToken.None));

                // A fresh store instance reads the same state back — durable across a process restart, not held
                // only in memory.
                var restarted = new PostgresTaskStore(Options.Create(new FactoryOptions { ConnectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING")! }), new TestClock());
                var globalPause = await restarted.GetDispatchPauseAsync(DispatchPauseScope.Global, CancellationToken.None);
                Assert.True(globalPause.Paused);
                Assert.Equal("Reserving capacity for interactive use", globalPause.Reason);
                Assert.NotNull(globalPause.PausedAt);
                Assert.Equal("operator", globalPause.PausedBy);

                var all = await restarted.GetAllDispatchPausesAsync(CancellationToken.None);
                Assert.Contains(all, p => p.Scope == DispatchPauseScope.Global && p.Paused);
                Assert.Contains(all, p => p.Scope == "Codex" && p.Paused);

                // Resuming clears the reason/actor/time along with the flag, so a later query never shows a
                // stale reason for a pause that is no longer in effect.
                await fixture.Tasks.SetDispatchPauseAsync(DispatchPauseScope.Global, false, null, "operator", CancellationToken.None);
                var resumed = await fixture.Tasks.GetDispatchPauseAsync(DispatchPauseScope.Global, CancellationToken.None);
                Assert.False(resumed.Paused);
                Assert.Null(resumed.Reason);
                Assert.Null(resumed.PausedAt);
                Assert.Null(resumed.PausedBy);
                Assert.False(await fixture.Tasks.IsDispatchPausedAsync(CancellationToken.None));
            }
            finally
            {
                await fixture.Connection.ExecuteAsync("DELETE FROM factory.dispatch_pause WHERE scope IN (@global,'Codex')", new { global = DispatchPauseScope.Global });
            }
        }
    }

    [Fact]
    public async Task Resuming_waiting_work_never_resumes_it_onto_a_paused_provider()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('pause-resume-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@taskId,@repositoryId,'Paused-provider task','WaitingForQuota','main')
                """, new { taskId, repositoryId });

            // Codex is not at quota, but the operator paused it to reserve capacity for interactive use: it must
            // never be treated as "available" for resuming a waiting task, exactly like it never bypasses quota.
            await tasks.SetDispatchPauseAsync("Codex", true, "Interactive debugging session", "operator", CancellationToken.None);
            Assert.Equal(0, await tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None));
            Assert.Equal("WaitingForQuota", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@taskId", new { taskId }));

            // Resuming Codex makes the task eligible again.
            await tasks.SetDispatchPauseAsync("Codex", false, null, "operator", CancellationToken.None);
            Assert.Equal(1, await tasks.ResumeExpiredQuotaTasksAsync(["Codex"], CancellationToken.None));
            Assert.Equal("Pending", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@taskId", new { taskId }));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.dispatch_pause WHERE scope='Codex'");
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id=@taskId", new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Claim_ordering_honors_an_operator_set_priority()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('priority-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var lowId = Guid.NewGuid();
        var highId = Guid.NewGuid();
        try
        {
            // "low" is created first, so without a priority override, created_at ordering alone would claim it first.
            await connection.ExecuteAsync("INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@lowId,@repositoryId,'Low priority task','Pending','main')", new { lowId, repositoryId });
            await connection.ExecuteAsync("INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@highId,@repositoryId,'High priority task','Pending','main')", new { highId, repositoryId });

            await tasks.SetPriorityAsync(highId, int.MaxValue, CancellationToken.None);

            var claimed = await tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.Equal(highId, claimed?.Id);
            Assert.Equal(int.MaxValue, claimed?.Priority);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@lowId,@highId)", new { lowId, highId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Dependency_management_rejects_self_dependencies_not_found_tasks_and_cycles_and_is_idempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('dependency-management-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch)
                VALUES(@a,@repositoryId,'Task A','Pending','main'),(@b,@repositoryId,'Task B','Pending','main'),(@c,@repositoryId,'Task C','Pending','main')
                """, new { a, b, c, repositoryId });

            Assert.Equal(AddDependencyOutcome.SelfDependency, await tasks.AddDependencyAsync(a, a, CancellationToken.None));
            Assert.Equal(AddDependencyOutcome.TaskNotFound, await tasks.AddDependencyAsync(a, Guid.NewGuid(), CancellationToken.None));

            Assert.Equal(AddDependencyOutcome.Added, await tasks.AddDependencyAsync(b, a, CancellationToken.None)); // B depends on A
            Assert.Equal(AddDependencyOutcome.AlreadyExists, await tasks.AddDependencyAsync(b, a, CancellationToken.None));
            Assert.Equal(AddDependencyOutcome.Added, await tasks.AddDependencyAsync(c, b, CancellationToken.None)); // C depends on B, so A <- B <- C

            // A depending on C would close the cycle A <- B <- C <- A — rejected, checked transitively, not just
            // the direct edge (A does not directly depend on C's prerequisite chain, only through B).
            Assert.Equal(AddDependencyOutcome.WouldCreateCycle, await tasks.AddDependencyAsync(a, c, CancellationToken.None));

            var dependenciesOfB = await tasks.GetDependenciesAsync(b, CancellationToken.None);
            var dependency = Assert.Single(dependenciesOfB);
            Assert.Equal(a, dependency.DependsOnTaskId);
            Assert.Equal("Task A", dependency.DependsOnTitle);
            Assert.Equal(FactoryTaskStatus.Pending, dependency.DependsOnStatus);

            await tasks.RemoveDependencyAsync(b, a, CancellationToken.None);
            Assert.Empty(await tasks.GetDependenciesAsync(b, CancellationToken.None));
            // Removing an edge that no longer exists is a harmless no-op.
            await tasks.RemoveDependencyAsync(b, a, CancellationToken.None);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_dependency WHERE task_id IN (@a,@b,@c) OR depends_on_task_id IN (@a,@b,@c)", new { a, b, c });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@a,@b,@c)", new { a, b, c });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Issue_dependency_reconciliation_adds_and_removes_only_its_own_edges_and_respects_cycle_rejection()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var owner = "issue-dependency-tests";
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES(@owner,@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { owner, suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });

        var prerequisiteId = Guid.NewGuid();
        var dependentId = Guid.NewGuid();
        var extraId = Guid.NewGuid();
        try
        {
            var prerequisiteIssueId = await InsertIssueAsync(connection, repositoryId, issueNumber: 1);
            var dependentIssueId = await InsertIssueAsync(connection, repositoryId, issueNumber: 2);
            var extraIssueId = await InsertIssueAsync(connection, repositoryId, issueNumber: 3);
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,github_issue_id,title,status,base_branch)
                VALUES(@prerequisiteId,@repositoryId,@prerequisiteIssueId,'Prerequisite task','Pending','main'),
                      (@dependentId,@repositoryId,@dependentIssueId,'Dependent task','Pending','main'),
                      (@extraId,@repositoryId,@extraIssueId,'Extra task','Pending','main')
                """, new { prerequisiteId, dependentId, extraId, repositoryId, prerequisiteIssueId, dependentIssueId, extraIssueId });

            // Resolves same-repository issue numbers to the tasks they produced; an unsynced issue number
            // resolves to null so the caller retries it on a later sync pass instead of erroring.
            Assert.Equal(prerequisiteId, await tasks.FindTaskIdForIssueAsync(owner, suffix, 1, CancellationToken.None));
            Assert.Null(await tasks.FindTaskIdForIssueAsync(owner, suffix, 999, CancellationToken.None));

            // First reconciliation: dependent's issue body declares "Depends on #1" -> one issue-sourced edge added.
            var first = await tasks.ReconcileIssueDependenciesAsync(dependentId, [prerequisiteId], CancellationToken.None);
            Assert.Equal([prerequisiteId], first.Added);
            Assert.Empty(first.Removed);
            Assert.Empty(first.SkippedCycles);
            var afterFirst = Assert.Single(await tasks.GetDependenciesAsync(dependentId, CancellationToken.None));
            Assert.Equal(prerequisiteId, afterFirst.DependsOnTaskId);
            Assert.Equal("issue", afterFirst.Source);

            // An operator also adds a manual edge (SF-611 dashboard) from the dependent to a third task.
            Assert.Equal(AddDependencyOutcome.Added, await tasks.AddDependencyAsync(dependentId, extraId, CancellationToken.None));

            // The operator edits the issue body to drop the "Depends on #1" line: re-reconciling with an empty
            // parsed set removes only the issue-sourced edge, never the manually-added one sitting alongside it.
            var second = await tasks.ReconcileIssueDependenciesAsync(dependentId, [], CancellationToken.None);
            Assert.Empty(second.Added);
            Assert.Equal([prerequisiteId], second.Removed);
            var afterSecond = Assert.Single(await tasks.GetDependenciesAsync(dependentId, CancellationToken.None));
            Assert.Equal(extraId, afterSecond.DependsOnTaskId);
            Assert.Null(afterSecond.Source);

            // A parsed reference that would close a cycle is skipped and reported, never inserted: the
            // prerequisite already (manually) depends on the dependent, so the dependent's own issue body
            // declaring "Depends on <prerequisite>" would close prerequisite <- dependent <- prerequisite.
            Assert.Equal(AddDependencyOutcome.Added, await tasks.AddDependencyAsync(prerequisiteId, dependentId, CancellationToken.None));
            var third = await tasks.ReconcileIssueDependenciesAsync(dependentId, [prerequisiteId], CancellationToken.None);
            Assert.Empty(third.Added);
            Assert.Equal([prerequisiteId], third.SkippedCycles);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_dependency WHERE task_id IN (@prerequisiteId,@dependentId,@extraId) OR depends_on_task_id IN (@prerequisiteId,@dependentId,@extraId)", new { prerequisiteId, dependentId, extraId });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@prerequisiteId,@dependentId,@extraId)", new { prerequisiteId, dependentId, extraId });
            await connection.ExecuteAsync("DELETE FROM github.issue WHERE repository_id=@repositoryId", new { repositoryId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    private static Task<long> InsertIssueAsync(NpgsqlConnection connection, long repositoryId, int issueNumber) =>
        connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.issue(repository_id,github_issue_id,issue_number,title,body,state,author,created_at,updated_at,last_synced_at)
            VALUES(@repositoryId,@issueNumber,@issueNumber,'Issue','Body','OPEN','alice',now(),now(),now()) RETURNING id
            """, new { repositoryId, issueNumber });

    [Fact]
    public async Task A_task_with_an_unmerged_prerequisite_is_never_claimed_until_the_prerequisite_merges()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('dependency-claim-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var prerequisiteId = Guid.NewGuid();
        var dependentId = Guid.NewGuid();
        try
        {
            // Maximum priority on both guards this test against any unrelated Pending task left over elsewhere
            // in this shared test database — these two must always be the ones actually contended over.
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch,priority)
                VALUES(@prerequisiteId,@repositoryId,'Prerequisite task','Pending','main',2147483647),
                      (@dependentId,@repositoryId,'Dependent task','Pending','main',2147483647)
                """, new { prerequisiteId, dependentId, repositoryId });
            Assert.Equal(AddDependencyOutcome.Added, await tasks.AddDependencyAsync(dependentId, prerequisiteId, CancellationToken.None));

            // The prerequisite has not merged: it is claimed first (both are equally high priority, so claim
            // order alone would otherwise be arbitrary between them), and the dependent is never claimed at
            // all — proving task B genuinely cannot start, and so cannot ever run against a base missing task A.
            var firstClaim = await tasks.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.Equal(prerequisiteId, firstClaim?.Id);
            Assert.Null(await tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(5), CancellationToken.None));

            // The prerequisite merges: the dependent becomes claimable.
            await connection.ExecuteAsync("UPDATE factory.task SET status='Completed' WHERE id=@prerequisiteId", new { prerequisiteId });
            var secondClaim = await tasks.ClaimNextAsync("worker-b", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.Equal(dependentId, secondClaim?.Id);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_dependency WHERE task_id=@dependentId", new { dependentId });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@prerequisiteId,@dependentId)", new { prerequisiteId, dependentId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task A_prerequisite_that_ends_without_merging_moves_its_dependent_to_needs_human()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('dependency-block-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var prerequisiteId = Guid.NewGuid();
        var dependentId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch)
                VALUES(@prerequisiteId,@repositoryId,'Prerequisite task','Pending','main'),(@dependentId,@repositoryId,'Dependent task','Pending','main')
                """, new { prerequisiteId, dependentId, repositoryId });
            await tasks.AddDependencyAsync(dependentId, prerequisiteId, CancellationToken.None);

            // The prerequisite is still pending: nothing to block yet.
            Assert.Equal(0, await tasks.BlockDependentsOnFailedPrerequisitesAsync(CancellationToken.None));

            // The prerequisite is cancelled — it will never merge, so the dependent must not be silently left
            // queued forever behind it, nor silently released to run against a base that will never contain it.
            await connection.ExecuteAsync("UPDATE factory.task SET status='Cancelled' WHERE id=@prerequisiteId", new { prerequisiteId });
            Assert.Equal(1, await tasks.BlockDependentsOnFailedPrerequisitesAsync(CancellationToken.None));

            var (status, reason) = await connection.QuerySingleAsync<(string Status, string Reason)>(
                "SELECT status AS \"Status\",failure_reason AS \"Reason\" FROM factory.task WHERE id=@dependentId", new { dependentId });
            Assert.Equal("NeedsHuman", status);
            Assert.Contains("Prerequisite task", reason);
            Assert.Contains("Cancelled", reason);

            // Idempotent: the dependent is no longer Pending, so a second sweep matches nothing further.
            Assert.Equal(0, await tasks.BlockDependentsOnFailedPrerequisitesAsync(CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_dependency WHERE task_id=@dependentId", new { dependentId });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@prerequisiteId,@dependentId)", new { prerequisiteId, dependentId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Reaching_the_outstanding_review_backlog_limit_pauses_new_claims_until_review_work_resolves()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var baselineSettings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(baselineSettings).MigrateAsync(CancellationToken.None);
        var baselineStore = new PostgresTaskStore(baselineSettings, new TestClock());
        // The limit is set relative to whatever the shared test database already holds, so this test never
        // assumes it owns the only ReadyForPublish/Published rows in existence.
        var baseline = await baselineStore.CountOutstandingReviewWorkAsync(CancellationToken.None);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('review-backlog-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var reviewTaskId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@reviewTaskId,@repositoryId,'Awaiting review','ReadyForPublish','main')", new { reviewTaskId, repositoryId });
            // Maximum priority guards this test against any unrelated Pending task left over elsewhere in this
            // shared test database — if anything is claimable, this is the row that would be claimed first.
            await connection.ExecuteAsync("INSERT INTO factory.task(id,repository_id,title,status,base_branch,priority) VALUES(@pendingId,@repositoryId,'New implementation','Pending','main',2147483647)", new { pendingId, repositoryId });

            // The limit exactly equals the current backlog (baseline plus the one ReadyForPublish task just
            // added): reaching it must pause a brand-new Pending claim, however high its priority.
            var gatedSettings = Options.Create(new FactoryOptions { ConnectionString = connectionString, MaxOutstandingReviewWork = baseline + 1 });
            var gatedStore = new PostgresTaskStore(gatedSettings, new TestClock());
            Assert.Equal(baseline + 1, await gatedStore.CountOutstandingReviewWorkAsync(CancellationToken.None));
            Assert.Null(await gatedStore.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None));

            // The review task resolves (merged) — capacity frees, and the same store now claims the pending task.
            await connection.ExecuteAsync("UPDATE factory.task SET status='Completed' WHERE id=@reviewTaskId", new { reviewTaskId });
            var claimed = await gatedStore.ClaimNextAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.Equal(pendingId, claimed?.Id);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@reviewTaskId,@pendingId)", new { reviewTaskId, pendingId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Outstanding_review_work_is_counted_from_task_status_never_inflated_by_repeated_publication_attempts()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('review-count-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var readyId = Guid.NewGuid();
        var publishedId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        try
        {
            var baseline = await tasks.CountOutstandingReviewWorkAsync(CancellationToken.None);
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch)
                VALUES(@readyId,@repositoryId,'Ready','ReadyForPublish','main'),
                      (@publishedId,@repositoryId,'Published','Published','main'),
                      (@pendingId,@repositoryId,'Pending','Pending','main'),
                      (@completedId,@repositoryId,'Completed','Completed','main')
                """, new { readyId, publishedId, pendingId, completedId, repositoryId });
            // The published task has two publication rows — a first attempt an earlier crashed worker never
            // recorded completion for, reclaimed and superseded by a second, successful one — proving the count
            // comes from factory.task.status, never from summing factory.publication rows (which would double it).
            await connection.ExecuteAsync("""
                INSERT INTO factory.publication(id,task_id,status,requested_by) VALUES
                  (@first,@publishedId,'Publishing','operator'),(@second,@publishedId,'PullRequestCreated','operator')
                """, new { first = Guid.NewGuid(), second = Guid.NewGuid(), publishedId });

            // Only ReadyForPublish and Published count — Pending and Completed never do — and exactly once each.
            Assert.Equal(baseline + 2, await tasks.CountOutstandingReviewWorkAsync(CancellationToken.None));

            // Restart-safe: a freshly constructed store (simulating a process restart, no in-memory state to
            // carry over) queries current database state and reaches the identical answer.
            var restarted = new PostgresTaskStore(Options.Create(new FactoryOptions { ConnectionString = connectionString }), new TestClock());
            Assert.Equal(baseline + 2, await restarted.CountOutstandingReviewWorkAsync(CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.publication WHERE task_id=@publishedId", new { publishedId });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@readyId,@publishedId,@pendingId,@completedId)", new { readyId, publishedId, pendingId, completedId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Continuing_with_feedback_resets_the_implementation_attempt_budget_and_records_the_feedback()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('continue-feedback-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@taskId,@repositoryId,'Exhausted task','Failed','main')", new { taskId, repositoryId });

            // Two prior implementation attempts, exhausting a budget of (say) 2.
            for (var i = 1; i <= 2; i++)
            {
                var runId = await tasks.StartRunAsync(taskId, "integration-worker", CancellationToken.None);
                var stepId = await tasks.StartStepAsync(runId, "AgentImplementation", 1, CancellationToken.None);
                await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), taskId, runId, stepId, "Codex", DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow, 1, 1, "Failed", "out", "still broken", false, null, i, false, null), CancellationToken.None);
            }
            Assert.Equal(2, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));

            Assert.True(await tasks.ContinueWithFeedbackAsync(taskId, "The CSV export must quote fields containing commas.", CancellationToken.None));

            var status = await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@taskId", new { taskId });
            Assert.Equal("Pending", status);

            // The budget is reset — only runs since this feedback count, so a bounded fresh allowance is
            // granted, not an unlimited one and not a permanently exhausted one.
            Assert.Equal(0, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));

            var recorded = await tasks.GetFeedbackAsync(taskId, CancellationToken.None);
            var entry = Assert.Single(recorded);
            Assert.Equal("The CSV export must quote fields containing commas.", entry.Body);
            Assert.Equal("operator", entry.CreatedBy);

            // A fresh attempt after the continuation counts toward the new cycle only.
            var newRunId = await tasks.StartRunAsync(taskId, "integration-worker", CancellationToken.None);
            var newStepId = await tasks.StartStepAsync(newRunId, "AgentImplementation", 1, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), taskId, newRunId, newStepId, "Codex", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, 1, 0, "Succeeded", "out", "", false, null, 1, false, null), CancellationToken.None);
            Assert.Equal(1, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_feedback WHERE task_id=@taskId", new { taskId });
            await connection.ExecuteAsync("""
                DELETE FROM factory.agent_run WHERE task_id=@taskId;
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id=@taskId);
                DELETE FROM factory.run WHERE task_id=@taskId;
                DELETE FROM factory.task WHERE id=@taskId;
                """, new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Continuation_is_allowed_from_resting_states_including_an_open_pull_request_but_refused_mid_execution()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('continue-statuses-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var publishedId = Guid.NewGuid();
        var implementingId = Guid.NewGuid();
        try
        {
            // Published (an open pull request) is a valid source: a manual-test failure found on already-published
            // work must be able to continue on the exact same branch, so a later publish updates the same PR.
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch,branch_name,worktree_path)
                VALUES(@publishedId,@repositoryId,'Published task','Published','main','factory/existing-branch','/factory/worktrees/existing')
                """, new { publishedId, repositoryId });
            Assert.True(await tasks.ContinueWithFeedbackAsync(publishedId, "The reviewer found an off-by-one error.", CancellationToken.None));
            var (status, branchName, worktreePath) = await connection.QuerySingleAsync<(string Status, string BranchName, string WorktreePath)>(
                "SELECT status AS \"Status\",branch_name AS \"BranchName\",worktree_path AS \"WorktreePath\" FROM factory.task WHERE id=@publishedId", new { publishedId });
            Assert.Equal("Pending", status);
            // The existing branch/worktree are left completely untouched — continuing keeps working on the same
            // changes rather than starting over, so a later publish never creates a duplicate pull request.
            Assert.Equal("factory/existing-branch", branchName);
            Assert.Equal("/factory/worktrees/existing", worktreePath);

            // A task actively executing is never a valid continuation target — the operator should wait or cancel.
            await connection.ExecuteAsync("INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@implementingId,@repositoryId,'Implementing task','Implementing','main')", new { implementingId, repositoryId });
            Assert.False(await tasks.ContinueWithFeedbackAsync(implementingId, "Too early.", CancellationToken.None));
            Assert.Empty(await tasks.GetFeedbackAsync(implementingId, CancellationToken.None));
            Assert.Equal("Implementing", await connection.ExecuteScalarAsync<string>("SELECT status FROM factory.task WHERE id=@implementingId", new { implementingId }));

            // A nonexistent task is refused, not thrown.
            Assert.False(await tasks.ContinueWithFeedbackAsync(Guid.NewGuid(), "Nothing to see here.", CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_feedback WHERE task_id IN (@publishedId,@implementingId)", new { publishedId, implementingId });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@publishedId,@implementingId)", new { publishedId, implementingId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Ci_status_is_fully_overwritten_on_each_sync_so_a_stale_head_commit_never_survives_alongside_a_newer_one()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('ci-status-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@taskId,@repositoryId,'Published task','Published','main')", new { taskId, repositoryId });

            Assert.Null(await tasks.GetCiStatusAsync(taskId, CancellationToken.None));

            var firstChecks = new List<PullRequestCheck> { new("Backend build and tests", PullRequestCiStatus.Pending, "https://example.invalid/1") };
            await tasks.SetCiStatusAsync(taskId, PullRequestCiStatus.Pending, "commit-1", firstChecks, null, CancellationToken.None);
            var afterFirst = await tasks.GetCiStatusAsync(taskId, CancellationToken.None);
            Assert.NotNull(afterFirst);
            Assert.Equal("commit-1", afterFirst!.HeadCommit);
            Assert.Equal(PullRequestCiStatus.Pending, afterFirst.OverallStatus);
            var firstCheck = Assert.Single(afterFirst.Checks);
            Assert.Equal("Backend build and tests", firstCheck.Name);

            // A later poll observes a new commit (e.g. an SF-613 continuation republished) with different checks —
            // this must fully replace the previous poll's result, never merge with or sit alongside it.
            var secondChecks = new List<PullRequestCheck>
            {
                new("Backend build and tests", PullRequestCiStatus.Success, "https://example.invalid/1"),
                new("Frontend lint", PullRequestCiStatus.Success, "https://example.invalid/2")
            };
            await tasks.SetCiStatusAsync(taskId, PullRequestCiStatus.Success, "commit-2", secondChecks, null, CancellationToken.None);
            var afterSecond = await tasks.GetCiStatusAsync(taskId, CancellationToken.None);
            Assert.NotNull(afterSecond);
            Assert.Equal("commit-2", afterSecond!.HeadCommit);
            Assert.Equal(PullRequestCiStatus.Success, afterSecond.OverallStatus);
            Assert.Equal(2, afterSecond.Checks.Count);

            // A read failure (authentication, network) is recorded explicitly, not silently dropped or confused
            // with "no checks configured."
            await tasks.SetCiStatusAsync(taskId, PullRequestCiStatus.Unavailable, null, [], "gh: authentication required", CancellationToken.None);
            var afterFailure = await tasks.GetCiStatusAsync(taskId, CancellationToken.None);
            Assert.NotNull(afterFailure);
            Assert.Equal(PullRequestCiStatus.Unavailable, afterFailure!.OverallStatus);
            Assert.Null(afterFailure.HeadCommit);
            Assert.Empty(afterFailure.Checks);
            Assert.Contains("authentication required", afterFailure.Error);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_ci_status WHERE task_id=@taskId", new { taskId });
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id=@taskId", new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Outcome_metrics_attribute_a_representative_history_correctly_including_fallback_and_rejected_work_and_respect_the_window()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('outcome-metrics-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var eventsTaskId = Guid.NewGuid();
        var ciSuccessId = Guid.NewGuid();
        var ciFailureId = Guid.NewGuid();
        var ciOldId = Guid.NewGuid();
        var reviewId1 = Guid.NewGuid();
        var reviewId2 = Guid.NewGuid();
        var reviewOldId = Guid.NewGuid();
        var allTaskIds = new[] { eventsTaskId, ciSuccessId, ciFailureId, ciOldId, reviewId1, reviewId2, reviewOldId };
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch)
                SELECT unnest(@ids),@repositoryId,'Outcome metrics task','Pending','main'
                """, new { ids = allTaskIds, repositoryId });

            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var inWindow = DateTimeOffset.UtcNow.AddDays(-1);
            var outsideWindow = DateTimeOffset.UtcNow.AddDays(-30);

            // Representative task_event history: fallback (quota wait then resume, excluded from Retries),
            // a human intervention followed by a genuine retry, and both merged and rejected outcomes — plus
            // one event well outside the window, to prove it is never counted.
            await connection.ExecuteAsync("""
                INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor,occurred_at) VALUES
                  (@eventsTaskId,'Validating','ReadyForPublish','validated','orchestrator',@inWindow),
                  (@eventsTaskId,'Published','Completed',NULL,'orchestrator',@inWindow),
                  (@eventsTaskId,'Published','Rejected','closed without merge','orchestrator',@inWindow),
                  (@eventsTaskId,'Failed','Pending','Retried by operator','human',@inWindow),
                  (@eventsTaskId,'WaitingForQuota','Pending',NULL,'orchestrator',@inWindow),
                  (@eventsTaskId,'Implementing','WaitingForQuota',NULL,'orchestrator',@inWindow),
                  (@eventsTaskId,'Planning','NeedsHuman','ambiguous requirement','orchestrator',@inWindow),
                  (@eventsTaskId,'Validating','ReadyForPublish','validated (stale)','orchestrator',@outsideWindow)
                """, new { eventsTaskId, inWindow, outsideWindow });

            var runId = await tasks.StartRunAsync(eventsTaskId, "integration-worker", CancellationToken.None);
            var succeededStepId = await tasks.StartStepAsync(runId, "AgentImplementation", 1, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), eventsTaskId, runId, succeededStepId, "Codex", inWindow, inWindow, 1, 0, "Succeeded", "", "", false, null, 1, false, null), CancellationToken.None);
            var failedStepId = await tasks.StartStepAsync(runId, "AgentImplementation", 2, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), eventsTaskId, runId, failedStepId, "Codex", inWindow, inWindow, 1, 1, "Failed", "", "boom", false, null, 2, false, null), CancellationToken.None);
            var quotaStepId = await tasks.StartStepAsync(runId, "AgentImplementation", 3, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), eventsTaskId, runId, quotaStepId, "Codex", inWindow, inWindow, 1, 1, "Failed", "", "rate limited", true, null, 3, false, null, CountsAsImplementationAttempt: false), CancellationToken.None);
            var oldStepId = await tasks.StartStepAsync(runId, "AgentImplementation", 4, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), eventsTaskId, runId, oldStepId, "Codex", outsideWindow, outsideWindow, 1, 0, "Succeeded", "", "", false, null, 4, false, null), CancellationToken.None);

            await connection.ExecuteAsync("""
                INSERT INTO factory.task_ci_status(task_id,head_commit,overall_status,checks_json,error,synced_at) VALUES
                  (@ciSuccessId,'c1','Success','[]',NULL,@inWindow),
                  (@ciFailureId,'c2','Failure','[]',NULL,@inWindow),
                  (@ciOldId,'c3','Success','[]',NULL,@outsideWindow)
                """, new { ciSuccessId, ciFailureId, ciOldId, inWindow, outsideWindow });

            await tasks.SetReviewMinutesAsync(reviewId1, 30, CancellationToken.None);
            await tasks.SetReviewMinutesAsync(reviewId2, 50, CancellationToken.None);
            // Backdate this one outside the window directly, since SetReviewMinutesAsync always stamps "now".
            await connection.ExecuteAsync("UPDATE factory.task SET review_minutes=99,review_recorded_at=@outsideWindow WHERE id=@reviewOldId", new { reviewOldId, outsideWindow });

            var metrics = await tasks.GetOutcomeMetricsAsync(since, CancellationToken.None);

            Assert.Equal(1, metrics.ValidatedReadyForReview); // the stale, outside-window one is excluded
            Assert.Equal(1, metrics.MergedAccepted);
            Assert.Equal(1, metrics.Rejected);
            Assert.Equal(1, metrics.Retries); // only the Failed->Pending one; WaitingForQuota->Pending is excluded
            Assert.Equal(1, metrics.QuotaWaitingEvents);
            Assert.Equal(1, metrics.HumanInterventions);
            Assert.Equal(1, metrics.AgentProcessSuccesses); // the outside-window success is excluded
            Assert.Equal(1, metrics.AgentProcessFailures); // the quota-interrupted failure never counts as either
            Assert.Equal(1, metrics.CiSuccesses); // the outside-window success is excluded
            Assert.Equal(1, metrics.CiFailures);
            Assert.Equal(2, metrics.ReviewedTaskCount); // the outside-window entry is excluded
            Assert.Equal(40, metrics.AverageReviewMinutes);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task_ci_status WHERE task_id=ANY(@allTaskIds)", new { allTaskIds });
            await connection.ExecuteAsync("""
                DELETE FROM factory.task_event WHERE task_id=ANY(@allTaskIds);
                DELETE FROM factory.agent_run WHERE task_id=ANY(@allTaskIds);
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id=ANY(@allTaskIds));
                DELETE FROM factory.run WHERE task_id=ANY(@allTaskIds);
                DELETE FROM factory.task WHERE id=ANY(@allTaskIds);
                """, new { allTaskIds });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Quota_interruptions_are_excluded_from_the_implementation_attempt_budget_but_real_failures_are_not()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('attempt-classification-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@taskId,@repositoryId,'Attempt classification task','Implementing','main')
                """, new { taskId, repositoryId });

            var runId = await tasks.StartRunAsync(taskId, "integration-worker", CancellationToken.None);
            var now = DateTimeOffset.UtcNow;

            // Three quota interruptions in a row must not consume any of the implementation-attempt budget.
            for (var i = 0; i < 3; i++)
            {
                var stepId = await tasks.StartStepAsync(runId, "AgentImplementation", i + 1, CancellationToken.None);
                await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), taskId, runId, stepId, "Codex",
                    now.AddMinutes(-10 + i), now.AddMinutes(-10 + i), 1, 1, "Failed", "", "quota", true, now.AddHours(5), i + 1, false, null,
                    CountsAsImplementationAttempt: false), CancellationToken.None);
            }
            Assert.Equal(0, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));
            Assert.Equal(3, await tasks.CountQuotaInterruptionsAsync(taskId, CancellationToken.None));
            Assert.Null(await tasks.GetPreviousAttemptAsync(taskId, CancellationToken.None));

            // A genuine, failed implementation attempt after those interruptions does count, and is what
            // GetPreviousAttemptAsync reports even though the quota interruptions above are more recent overall.
            var realStepId = await tasks.StartStepAsync(runId, "AgentImplementation", 4, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), taskId, runId, realStepId, "Codex", now.AddMinutes(-5), now.AddMinutes(-5),
                1, 0, "Succeeded", "out", "", false, null, 4, false,
                new AgentResult("completed", "Real attempt summary", ["dotnet test"], true, ["src/Real.cs"], [], false, null)), CancellationToken.None);

            Assert.Equal(1, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));
            Assert.Equal(3, await tasks.CountQuotaInterruptionsAsync(taskId, CancellationToken.None));
            var previous = await tasks.GetPreviousAttemptAsync(taskId, CancellationToken.None);
            Assert.NotNull(previous);
            Assert.Equal("Real attempt summary", previous!.AgentSummary);
            Assert.Contains("src/Real.cs", previous.ChangedFiles);

            // A further real (non-quota) failure does consume the budget.
            var secondRealStepId = await tasks.StartStepAsync(runId, "AgentImplementation", 5, CancellationToken.None);
            await tasks.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), taskId, runId, secondRealStepId, "Codex", now, now,
                1, 1, "Failed", "", "compile error", false, null, 5, false, null), CancellationToken.None);
            Assert.Equal(2, await tasks.CountAgentRunsAsync(taskId, CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM factory.agent_run WHERE task_id=@taskId;
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id=@taskId);
                DELETE FROM factory.run WHERE task_id=@taskId;
                DELETE FROM factory.task WHERE id=@taskId;
                """, new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Starting_a_step_persists_its_deterministic_log_path()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var factoryOptions = new FactoryOptions { ConnectionString = connectionString, LogsDirectory = "/tmp/factory-log-tests" };
        var settings = Options.Create(factoryOptions);
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var suffix = Guid.NewGuid().ToString("N");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('log-path-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@taskId,@repositoryId,'Log path task','Implementing','main')
                """, new { taskId, repositoryId });
            var runId = await tasks.StartRunAsync(taskId, "integration-worker", CancellationToken.None);

            var stepId = await tasks.StartStepAsync(runId, "AgentImplementation", 1, CancellationToken.None);

            var logPath = await connection.ExecuteScalarAsync<string>("SELECT log_path FROM factory.step WHERE id=@stepId", new { stepId });
            Assert.Equal(StepLogPaths.Resolve(factoryOptions.LogsDirectory, runId, stepId), logPath);
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id=@taskId);
                DELETE FROM factory.run WHERE task_id=@taskId;
                DELETE FROM factory.task WHERE id=@taskId;
                """, new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Worker_heartbeat_upserts_host_and_current_task_and_clears_it_when_idle()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());
        var workerId = $"worker-{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('heartbeat-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var taskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch) VALUES(@taskId,@repositoryId,'Heartbeat task','Implementing','main')
                """, new { taskId, repositoryId });

            await tasks.RecordHeartbeatAsync(workerId, "host-a", taskId, CancellationToken.None);
            var busy = await connection.QuerySingleAsync<(string Host, Guid? CurrentTaskId)>(
                "SELECT host,current_task_id AS \"CurrentTaskId\" FROM factory.worker WHERE worker_id=@workerId", new { workerId });
            Assert.Equal("host-a", busy.Host);
            Assert.Equal(taskId, busy.CurrentTaskId);

            await tasks.RecordHeartbeatAsync(workerId, "host-a", null, CancellationToken.None);
            var idle = await connection.QuerySingleAsync<Guid?>("SELECT current_task_id FROM factory.worker WHERE worker_id=@workerId", new { workerId });
            Assert.Null(idle);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.worker WHERE worker_id=@workerId", new { workerId });
            await connection.ExecuteAsync("""
                DELETE FROM factory.task WHERE id=@taskId;
                """, new { taskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    [Fact]
    public async Task Worktree_cleanup_candidates_exclude_active_tasks_and_the_conditional_clear_only_applies_when_status_is_unchanged()
    {
        var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
        await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
        var tasks = new PostgresTaskStore(settings, new TestClock());

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var repositoryId = await connection.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
            VALUES('worktree-cleanup-tests',@suffix,@cloneUrl,'main',true) RETURNING id
            """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
        var restingTaskId = Guid.NewGuid();
        var activeTaskId = Guid.NewGuid();
        var noWorktreeTaskId = Guid.NewGuid();
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch,worktree_path,branch_name)
                VALUES(@restingTaskId,@repositoryId,'Resting task','Completed','main','/factory/worktrees/acme/w/issue-1','factory/1-x'),
                      (@activeTaskId,@repositoryId,'Active task','Implementing','main','/factory/worktrees/acme/w/issue-2','factory/2-x'),
                      (@noWorktreeTaskId,@repositoryId,'No worktree task','Failed','main',NULL,NULL)
                """, new { restingTaskId, activeTaskId, noWorktreeTaskId, repositoryId });

            var candidates = await tasks.GetWorktreeCleanupCandidatesAsync(CancellationToken.None);
            var candidateIds = candidates.Select(c => c.TaskId).ToHashSet();
            Assert.Contains(restingTaskId, candidateIds);
            Assert.DoesNotContain(activeTaskId, candidateIds);
            Assert.DoesNotContain(noWorktreeTaskId, candidateIds);
            var restingCandidate = candidates.Single(c => c.TaskId == restingTaskId);
            Assert.Equal(FactoryTaskStatus.Completed, restingCandidate.Status);
            Assert.Equal("/factory/worktrees/acme/w/issue-1", restingCandidate.WorktreePath);

            // A stale expected status (the active task's real status is Implementing) never clears anything.
            Assert.False(await tasks.ClearWorkspaceIfStatusUnchangedAsync(activeTaskId, FactoryTaskStatus.Completed, CancellationToken.None));
            var stillActive = await connection.ExecuteScalarAsync<string>("SELECT worktree_path FROM factory.task WHERE id=@activeTaskId", new { activeTaskId });
            Assert.NotNull(stillActive);

            Assert.True(await tasks.ClearWorkspaceIfStatusUnchangedAsync(restingTaskId, FactoryTaskStatus.Completed, CancellationToken.None));
            var (clearedPath, clearedBranch) = await connection.QuerySingleAsync<(string? Path, string? Branch)>(
                "SELECT worktree_path AS \"Path\",branch_name AS \"Branch\" FROM factory.task WHERE id=@restingTaskId", new { restingTaskId });
            Assert.Null(clearedPath);
            Assert.Null(clearedBranch);

            // Now that it's already cleared, the same call again is a no-op (status matches but worktree_path is already null; nothing to touch).
            var candidatesAfterClear = await tasks.GetWorktreeCleanupCandidatesAsync(CancellationToken.None);
            Assert.DoesNotContain(restingTaskId, candidatesAfterClear.Select(c => c.TaskId));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM factory.task WHERE id IN (@restingTaskId,@activeTaskId,@noWorktreeTaskId)", new { restingTaskId, activeTaskId, noWorktreeTaskId });
            await connection.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
        }
    }

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    private sealed class LeaseFixture : IAsyncDisposable
    {
        private LeaseFixture(NpgsqlConnection connection, PostgresTaskStore tasks, long repositoryId, Guid taskId)
        {
            Connection = connection;
            Tasks = tasks;
            RepositoryId = repositoryId;
            TaskId = taskId;
        }

        public NpgsqlConnection Connection { get; }
        public PostgresTaskStore Tasks { get; }
        public long RepositoryId { get; }
        public Guid TaskId { get; }

        public static async Task<LeaseFixture?> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(connectionString)) return null;
            var settings = Options.Create(new FactoryOptions { ConnectionString = connectionString });
            await new DatabaseMigrator(settings).MigrateAsync(CancellationToken.None);
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var suffix = Guid.NewGuid().ToString("N");
            var repositoryId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO github.repository(owner,name,clone_url,default_branch,is_enabled)
                VALUES('lease-tests',@suffix,@cloneUrl,'main',true) RETURNING id
                """, new { suffix, cloneUrl = $"https://example.invalid/{suffix}.git" });
            var taskId = Guid.NewGuid();
            await connection.ExecuteAsync("""
                INSERT INTO factory.task(id,repository_id,title,status,base_branch,priority)
                VALUES(@taskId,@repositoryId,'Lease integration task','Pending','main',2147483647)
                """, new { taskId, repositoryId });
            return new LeaseFixture(connection, new PostgresTaskStore(settings, new TestClock()), repositoryId, taskId);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.ExecuteAsync("""
                DELETE FROM factory.agent_run WHERE task_id=@TaskId;
                DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id=@TaskId);
                DELETE FROM factory.run WHERE task_id=@TaskId;
                DELETE FROM factory.task WHERE id=@TaskId;
                DELETE FROM github.repository WHERE id=@RepositoryId;
                """, new { TaskId, RepositoryId });
            await Connection.DisposeAsync();
        }
    }
}
