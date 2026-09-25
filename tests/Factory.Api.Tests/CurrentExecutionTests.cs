using System.Net;
using System.Text.Json;
using Dapper;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.Api.Tests;

[CollectionDefinition("API PostgreSQL tests", DisableParallelization = true)]
public sealed class ApiPostgresCollectionDefinition { }

public sealed class CurrentExecutionProjectionTests
{
    [Fact]
    public void Missing_execution_returns_an_explicit_idle_snapshot_without_stale_references()
    {
        var snapshot = CurrentExecutionProjection.Create(null, "http://localhost:3000/", DateTimeOffset.UtcNow);

        Assert.Equal("Idle", snapshot.Status);
        Assert.Null(snapshot.TaskId);
        Assert.Null(snapshot.RunId);
        Assert.Null(snapshot.StepId);
        Assert.Null(snapshot.StepType);
        Assert.Null(snapshot.Agent);
        Assert.Null(snapshot.LastProgressAt);
    }

    [Fact]
    public void Starting_without_a_run_does_not_expose_a_stale_selected_agent()
    {
        var snapshot = CurrentExecutionProjection.Create(new CurrentExecutionRow
        {
            TaskId = Guid.NewGuid(),
            TaskStatus = "Preparing",
            CurrentAgent = "Claude"
        }, "http://localhost:3000", DateTimeOffset.UtcNow);

        Assert.Equal("Starting", snapshot.Status);
        Assert.Null(snapshot.RunId);
        Assert.Null(snapshot.Agent);
    }

    [Fact]
    public void Projection_uses_fallback_agent_and_distinguishes_stopping_from_the_task_status()
    {
        var now = DateTimeOffset.UtcNow;
        var stepStartedAt = now.AddSeconds(-18);
        var snapshot = CurrentExecutionProjection.Create(new CurrentExecutionRow
        {
            TaskId = Guid.NewGuid(),
            TaskTitle = "Exercise fallback",
            TaskStatus = "Stopping",
            RepositoryOwner = "acme",
            RepositoryName = "factory",
            CurrentAgent = "Claude",
            LastAgent = "Codex-Sol",
            RunId = Guid.NewGuid(),
            RunStartedAt = now.AddMinutes(-1),
            StepId = Guid.NewGuid(),
            StepType = "AgentImplementation",
            StepStartedAt = stepStartedAt,
            StepAttempt = 2,
            MaxImplementationAttempts = 4
        }, "http://localhost:3000/", now);

        Assert.Equal("Stopping", snapshot.Status);
        Assert.Equal("Stopping", snapshot.TaskStatus);
        Assert.Equal("Claude", snapshot.Agent);
        Assert.Equal(2, snapshot.ImplementationAttempt);
        Assert.Equal(4, snapshot.MaxImplementationAttempts);
        Assert.Equal(stepStartedAt, snapshot.StartedAt);
        Assert.Equal(stepStartedAt, snapshot.LastProgressAt);
        Assert.InRange(snapshot.ElapsedSeconds!.Value, 17, 19);
    }

    [Fact]
    public void Projection_shows_provider_for_a_legacy_codex_invocation()
    {
        var snapshot = CurrentExecutionProjection.Create(new CurrentExecutionRow
        {
            TaskId = Guid.NewGuid(), TaskTitle = "Legacy Codex task", TaskStatus = "Implementing",
            RepositoryOwner = "acme", RepositoryName = "factory", LastAgent = "Codex-Sol",
            RunId = Guid.NewGuid(), RunStartedAt = DateTimeOffset.UtcNow
        }, "http://localhost:3000", DateTimeOffset.UtcNow);

        Assert.Equal("Codex", snapshot.Agent);
    }
}

[Collection("API PostgreSQL tests")]
public sealed class CurrentExecutionEndpointTests : IClassFixture<RootEndpointTests.FactoryApplication>, IAsyncLifetime
{
    private readonly HttpClient client;
    private readonly string connectionString;
    private NpgsqlDataSource? dataSource;
    private long? repositoryId;
    private long? issueId;
    private Guid? taskId;
    private Guid? previousRunId;
    private Guid? previousStepId;
    private Guid? previousAgentRunId;
    private Guid? runId;
    private Guid? previousCurrentRunStepId;
    private Guid? currentStepId;
    private Guid? currentAgentRunId;
    private string? expectedRepository;
    private string? expectedIssueUrl;

    public CurrentExecutionEndpointTests(RootEndpointTests.FactoryApplication application)
    {
        client = application.CreateClient();
        connectionString = Environment.GetEnvironmentVariable("FACTORY_TEST_CONNECTION_STRING") ?? new FactoryOptions().ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await new DatabaseMigrator(Options.Create(new FactoryOptions { ConnectionString = connectionString })).MigrateAsync(CancellationToken.None);
        dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    public async Task DisposeAsync()
    {
        if (dataSource is not null)
        {
            await using var c = await dataSource.OpenConnectionAsync();
            if (taskId is not null)
            {
                await c.ExecuteAsync("DELETE FROM factory.agent_run WHERE task_id=@taskId", new { taskId });
                await c.ExecuteAsync("DELETE FROM factory.step WHERE run_id IN (SELECT id FROM factory.run WHERE task_id=@taskId)", new { taskId });
                await c.ExecuteAsync("DELETE FROM factory.run WHERE task_id=@taskId", new { taskId });
                await c.ExecuteAsync("DELETE FROM factory.task WHERE id=@taskId", new { taskId });
            }
            if (issueId is not null) await c.ExecuteAsync("DELETE FROM github.issue WHERE id=@issueId", new { issueId });
            if (repositoryId is not null) await c.ExecuteAsync("DELETE FROM github.repository WHERE id=@repositoryId", new { repositoryId });
            await dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task Current_execution_reports_the_retried_run_and_fallback_agent_then_clears_finished_step()
    {
        await SeedRetryAndFallbackExecutionAsync();

        var running = await ReadSnapshotAsync();
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        Assert.Equal("Running", running.Document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Implementing", running.Document.RootElement.GetProperty("taskStatus").GetString());
        Assert.Equal(taskId!.Value, running.Document.RootElement.GetProperty("taskId").GetGuid());
        Assert.Equal(expectedRepository, running.Document.RootElement.GetProperty("repository").GetString());
        Assert.Equal(expectedIssueUrl, running.Document.RootElement.GetProperty("issueUrl").GetString());
        Assert.Equal("Claude", running.Document.RootElement.GetProperty("agent").GetString());
        Assert.Equal(runId!.Value, running.Document.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal(currentStepId!.Value, running.Document.RootElement.GetProperty("stepId").GetGuid());
        Assert.Equal("AgentImplementation", running.Document.RootElement.GetProperty("stepType").GetString());
        Assert.Equal(2, running.Document.RootElement.GetProperty("implementationAttempt").GetInt32());
        Assert.Equal(4, running.Document.RootElement.GetProperty("maxImplementationAttempts").GetInt32());
        Assert.Equal($"http://localhost:3000/tasks/{taskId}", running.Document.RootElement.GetProperty("taskUrl").GetString());
        Assert.True(running.Document.RootElement.TryGetProperty("issueNumber", out _));
        Assert.True(running.Document.RootElement.GetProperty("lastProgressAt").ValueKind == JsonValueKind.String);
        Assert.False(running.Document.RootElement.TryGetProperty("logPath", out _));
        running.Document.Dispose();

        await using (var c = await dataSource!.OpenConnectionAsync())
        {
            var completedAt = DateTimeOffset.UtcNow.AddMinutes(4).AddSeconds(3);
            await c.ExecuteAsync("UPDATE factory.step SET status='Succeeded',completed_at=@completedAt,duration_ms=100 WHERE id=@stepId", new { stepId = currentStepId, completedAt });
            await c.ExecuteAsync("UPDATE factory.task SET current_agent=NULL WHERE id=@taskId", new { taskId });
            currentAgentRunId = Guid.NewGuid();
            await c.ExecuteAsync("""
                INSERT INTO factory.agent_run(id,task_id,run_id,step_id,agent,started_at,completed_at,status,attempt_number,purpose)
                VALUES(@id,@taskId,@runId,@stepId,'Claude',@startedAt,@completedAt,'Succeeded',2,'Implement')
                """, new { id = currentAgentRunId, taskId, runId, stepId = currentStepId, startedAt = completedAt.AddSeconds(-1), completedAt });
        }

        var betweenSteps = await ReadSnapshotAsync();
        Assert.Equal("BetweenSteps", betweenSteps.Document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Claude", betweenSteps.Document.RootElement.GetProperty("agent").GetString());
        Assert.Equal(JsonValueKind.Null, betweenSteps.Document.RootElement.GetProperty("stepId").ValueKind);
        Assert.Equal(JsonValueKind.Null, betweenSteps.Document.RootElement.GetProperty("stepType").ValueKind);
        Assert.Equal(JsonValueKind.Null, betweenSteps.Document.RootElement.GetProperty("stepStartedAt").ValueKind);
        Assert.False(betweenSteps.Document.RootElement.TryGetProperty("logPath", out _));
        betweenSteps.Document.Dispose();

        await using (var c = await dataSource!.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE factory.run SET status='Failed',completed_at=now() WHERE id=@runId", new { runId });
            await c.ExecuteAsync("UPDATE factory.task SET status='Preparing' WHERE id=@taskId", new { taskId });
        }

        var starting = await ReadSnapshotAsync();
        Assert.Equal("Starting", starting.Document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, starting.Document.RootElement.GetProperty("runId").ValueKind);
        Assert.Equal(JsonValueKind.Null, starting.Document.RootElement.GetProperty("stepId").ValueKind);
        starting.Document.Dispose();
    }

    private async Task SeedRetryAndFallbackExecutionAsync()
    {
        await using var c = await dataSource!.OpenConnectionAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = $"codex-test-{suffix[..8]}";
        const string name = "current-execution";
        expectedRepository = $"{owner}/{name}";
        expectedIssueUrl = $"https://github.com/{owner}/{name}/issues/113";
        repositoryId = await c.ExecuteScalarAsync<long>("""
            INSERT INTO github.repository(owner,name,clone_url,default_branch)
            VALUES(@owner,@name,@cloneUrl,'main') RETURNING id
            """, new { owner, name, cloneUrl = $"https://github.com/{owner}/{name}.git" });
        issueId = await c.ExecuteScalarAsync<long>("""
            INSERT INTO github.issue(repository_id,github_issue_id,issue_number,title,body,state,author,created_at,updated_at)
            VALUES(@repositoryId,@githubIssueId,113,'Current execution test','', 'open','test',now(),now()) RETURNING id
            """, new { repositoryId, githubIssueId = Random.Shared.NextInt64(1, long.MaxValue) });

        taskId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await c.ExecuteAsync("""
            INSERT INTO factory.task(id,repository_id,github_issue_id,title,description,status,preferred_agent,current_agent,
              base_branch,created_at,started_at,claimed_at)
            VALUES(@taskId,@repositoryId,@issueId,'Current execution test','', 'Implementing','Codex-Sol','Claude',
              'main',@createdAt,@startedAt,@claimedAt)
            """, new { taskId, repositoryId, issueId, createdAt = now.AddHours(-2), startedAt = now.AddHours(-1), claimedAt = now.AddMinutes(5) });

        previousRunId = Guid.NewGuid();
        previousStepId = Guid.NewGuid();
        previousAgentRunId = Guid.NewGuid();
        await c.ExecuteAsync("""
            INSERT INTO factory.run(id,task_id,started_at,completed_at,status,worker_id)
            VALUES(@runId,@taskId,@startedAt,@completedAt,'Cancelled','previous-worker')
            """, new { runId = previousRunId, taskId, startedAt = now.AddHours(-1), completedAt = now.AddMinutes(-55) });
        await c.ExecuteAsync("""
            INSERT INTO factory.step(id,run_id,step_type,status,started_at,completed_at,attempt)
            VALUES(@stepId,@runId,'AgentImplementation','Failed',@startedAt,@completedAt,1)
            """, new { stepId = previousStepId, runId = previousRunId, startedAt = now.AddHours(-1), completedAt = now.AddMinutes(-59) });
        await c.ExecuteAsync("""
            INSERT INTO factory.agent_run(id,task_id,run_id,step_id,agent,started_at,completed_at,status,attempt_number,purpose)
            VALUES(@id,@taskId,@runId,@stepId,'Codex-Sol',@startedAt,@completedAt,'Failed',1,'Implement')
            """, new { id = previousAgentRunId, taskId, runId = previousRunId, stepId = previousStepId, startedAt = now.AddHours(-1), completedAt = now.AddMinutes(-59) });

        runId = Guid.NewGuid();
        var futureRunStart = now.AddMinutes(4);
        await c.ExecuteAsync("""
            INSERT INTO factory.run(id,task_id,started_at,status,worker_id,repository_configuration)
            VALUES(@runId,@taskId,@startedAt,'Running','current-worker',CAST('{"maxImplementationAttempts":4}' AS jsonb))
            """, new { runId, taskId, startedAt = futureRunStart });

        previousCurrentRunStepId = Guid.NewGuid();
        currentStepId = Guid.NewGuid();
        await c.ExecuteAsync("""
            INSERT INTO factory.step(id,run_id,step_type,status,started_at,completed_at,attempt)
            VALUES(@stepId,@runId,'PrepareRepository','Succeeded',@startedAt,@completedAt,1)
            """, new { stepId = previousCurrentRunStepId, runId, startedAt = futureRunStart, completedAt = futureRunStart.AddSeconds(1) });
        await c.ExecuteAsync("""
            INSERT INTO factory.step(id,run_id,step_type,status,started_at,attempt)
            VALUES(@stepId,@runId,'AgentImplementation','Running',@startedAt,2)
            """, new { stepId = currentStepId, runId, startedAt = futureRunStart.AddSeconds(2) });
    }

    private async Task<(HttpStatusCode StatusCode, JsonDocument Document)> ReadSnapshotAsync()
    {
        var response = await client.GetAsync("/api/execution/current");
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document);
    }
}
