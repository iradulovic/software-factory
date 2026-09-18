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
            await fixture.Tasks.SetRunConfigurationAsync(runId, new RepositoryConfiguration("main", ["dotnet build"], ["dotnet test"], 3, 1, true), CancellationToken.None);

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
