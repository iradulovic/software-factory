using System.Net;
using System.Net.Http.Json;
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
        Assert.Null(snapshot.AgentRunId);
        Assert.Null(snapshot.AgentModel);
        Assert.Null(snapshot.AgentReasoningEffort);
        Assert.Equal("None", snapshot.AgentInvocationContext);
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
        Assert.Equal("None", snapshot.AgentInvocationContext);
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
            AgentRunId = Guid.NewGuid(),
            AgentRunAgent = "Claude",
            AgentRunModel = "claude-opus-4",
            AgentRunReasoningEffort = "high",
            AgentRunPurpose = "Implement",
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
        Assert.Equal("Active", snapshot.AgentInvocationContext);
        Assert.Equal("claude-opus-4", snapshot.AgentModel);
        Assert.Equal("high", snapshot.AgentReasoningEffort);
        Assert.Equal("Implement", snapshot.AgentPurpose);
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
            RepositoryOwner = "acme", RepositoryName = "factory", AgentRunId = Guid.NewGuid(), AgentRunAgent = "Codex-Sol",
            RunId = Guid.NewGuid(), RunStartedAt = DateTimeOffset.UtcNow
        }, "http://localhost:3000", DateTimeOffset.UtcNow);

        Assert.Equal("Codex", snapshot.Agent);
        Assert.Equal("Last", snapshot.AgentInvocationContext);
    }

    [Fact]
    public void Active_agent_without_a_matching_invocation_record_does_not_reuse_older_metadata()
    {
        var snapshot = CurrentExecutionProjection.Create(new CurrentExecutionRow
        {
            TaskId = Guid.NewGuid(), TaskStatus = "Reviewing", CurrentAgent = "Codex-Sol",
            AgentRunAgent = "Claude", AgentRunModel = "older-model",
            AgentRunReasoningEffort = "low", RunId = Guid.NewGuid(), StepId = Guid.NewGuid(), StepType = "AgentReviewFix"
        }, "http://localhost:3000", DateTimeOffset.UtcNow);

        Assert.Equal("Codex", snapshot.Agent);
        Assert.Equal("Pending", snapshot.AgentInvocationContext);
        Assert.Null(snapshot.AgentRunId);
        Assert.Null(snapshot.AgentModel);
        Assert.Null(snapshot.AgentReasoningEffort);
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
        Assert.Equal("Pending", running.Document.RootElement.GetProperty("agentInvocationContext").GetString());
        Assert.Equal(JsonValueKind.Null, running.Document.RootElement.GetProperty("agentModel").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.Document.RootElement.GetProperty("agentReasoningEffort").ValueKind);
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

        var completedAt = DateTimeOffset.UtcNow.AddMinutes(4).AddSeconds(3);
        await using (var c = await dataSource!.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE factory.step SET status='Succeeded',completed_at=@completedAt,duration_ms=100 WHERE id=@stepId", new { stepId = currentStepId, completedAt });
            await c.ExecuteAsync("UPDATE factory.task SET current_agent=NULL WHERE id=@taskId", new { taskId });
            currentAgentRunId = Guid.NewGuid();
            await c.ExecuteAsync("""
                INSERT INTO factory.agent_run(id,task_id,run_id,step_id,agent,started_at,completed_at,status,attempt_number,purpose,model,reasoning_effort)
                VALUES(@id,@taskId,@runId,@stepId,'Claude',@startedAt,@completedAt,'Succeeded',2,'Implement','claude-opus-4','high')
                """, new { id = currentAgentRunId, taskId, runId, stepId = currentStepId, startedAt = completedAt.AddSeconds(-1), completedAt });
        }

        var betweenSteps = await ReadSnapshotAsync();
        Assert.Equal("BetweenSteps", betweenSteps.Document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Claude", betweenSteps.Document.RootElement.GetProperty("agent").GetString());
        Assert.Equal("Last", betweenSteps.Document.RootElement.GetProperty("agentInvocationContext").GetString());
        Assert.Equal("claude-opus-4", betweenSteps.Document.RootElement.GetProperty("agentModel").GetString());
        Assert.Equal("high", betweenSteps.Document.RootElement.GetProperty("agentReasoningEffort").GetString());
        Assert.Equal("Implement", betweenSteps.Document.RootElement.GetProperty("agentPurpose").GetString());
        Assert.Equal(JsonValueKind.Null, betweenSteps.Document.RootElement.GetProperty("stepId").ValueKind);
        Assert.Equal(JsonValueKind.Null, betweenSteps.Document.RootElement.GetProperty("stepType").ValueKind);
        Assert.Equal(JsonValueKind.Null, betweenSteps.Document.RootElement.GetProperty("stepStartedAt").ValueKind);
        Assert.False(betweenSteps.Document.RootElement.TryGetProperty("logPath", out _));
        betweenSteps.Document.Dispose();

        Guid reviewStepId = Guid.NewGuid();
        await using (var c = await dataSource!.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE factory.step SET status='Succeeded',completed_at=@completedAt,duration_ms=100 WHERE id=@stepId", new { stepId = currentStepId, completedAt = completedAt.AddSeconds(1) });
            await c.ExecuteAsync("""
                INSERT INTO factory.step(id,run_id,step_type,status,started_at,attempt)
                VALUES(@stepId,@runId,'AgentReviewFix','Running',@startedAt,1)
                """, new { stepId = reviewStepId, runId, startedAt = completedAt.AddSeconds(2) });
            await c.ExecuteAsync("UPDATE factory.task SET current_agent='Codex-Sol',status='Reviewing' WHERE id=@taskId", new { taskId });
        }

        var reviewPending = await ReadSnapshotAsync();
        Assert.Equal("Pending", reviewPending.Document.RootElement.GetProperty("agentInvocationContext").GetString());
        Assert.Equal("Codex", reviewPending.Document.RootElement.GetProperty("agent").GetString());
        Assert.Equal(JsonValueKind.Null, reviewPending.Document.RootElement.GetProperty("agentModel").ValueKind);
        reviewPending.Document.Dispose();

        await using (var c = await dataSource!.OpenConnectionAsync())
        {
            var reviewAgentRunId = Guid.NewGuid();
            await c.ExecuteAsync("""
                INSERT INTO factory.agent_run(id,task_id,run_id,step_id,agent,started_at,completed_at,status,attempt_number,purpose,model,reasoning_effort)
                VALUES(@id,@taskId,@runId,@stepId,'Codex-Sol',@startedAt,@completedAt,'Succeeded',1,'Fix','gpt-6-sol','medium')
                """, new { id = reviewAgentRunId, taskId, runId, stepId = reviewStepId, startedAt = completedAt.AddSeconds(2), completedAt = completedAt.AddSeconds(3) });
        }

        var review = await ReadSnapshotAsync();
        Assert.Equal("Active", review.Document.RootElement.GetProperty("agentInvocationContext").GetString());
        Assert.Equal("Codex", review.Document.RootElement.GetProperty("agent").GetString());
        Assert.Equal("gpt-6-sol", review.Document.RootElement.GetProperty("agentModel").GetString());
        Assert.Equal("medium", review.Document.RootElement.GetProperty("agentReasoningEffort").GetString());
        Assert.Equal("Fix", review.Document.RootElement.GetProperty("agentPurpose").GetString());
        review.Document.Dispose();

        Guid buildStepId = Guid.NewGuid();
        await using (var c = await dataSource!.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE factory.step SET status='Succeeded',completed_at=@completedAt,duration_ms=100 WHERE id=@stepId", new { stepId = reviewStepId, completedAt = completedAt.AddSeconds(4) });
            await c.ExecuteAsync("""
                INSERT INTO factory.step(id,run_id,step_type,status,started_at,attempt)
                VALUES(@stepId,@runId,'Build','Running',@startedAt,1)
                """, new { stepId = buildStepId, runId, startedAt = completedAt.AddSeconds(5) });
            await c.ExecuteAsync("UPDATE factory.task SET current_agent=NULL,status='Validating' WHERE id=@taskId", new { taskId });
        }

        var build = await ReadSnapshotAsync();
        Assert.Equal("Build", build.Document.RootElement.GetProperty("stepType").GetString());
        Assert.Equal("Last", build.Document.RootElement.GetProperty("agentInvocationContext").GetString());
        Assert.Equal("Codex", build.Document.RootElement.GetProperty("agent").GetString());
        Assert.Equal("gpt-6-sol", build.Document.RootElement.GetProperty("agentModel").GetString());
        Assert.Equal("medium", build.Document.RootElement.GetProperty("agentReasoningEffort").GetString());
        Assert.Equal("Fix", build.Document.RootElement.GetProperty("agentPurpose").GetString());
        build.Document.Dispose();

        await using (var c = await dataSource!.OpenConnectionAsync())
        {
            await c.ExecuteAsync("UPDATE factory.run SET status='Failed',completed_at=now() WHERE id=@runId", new { runId });
            await c.ExecuteAsync("UPDATE factory.task SET status='Preparing' WHERE id=@taskId", new { taskId });
        }

        var starting = await ReadSnapshotAsync();
        Assert.Equal("Starting", starting.Document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, starting.Document.RootElement.GetProperty("runId").ValueKind);
        Assert.Equal(JsonValueKind.Null, starting.Document.RootElement.GetProperty("stepId").ValueKind);
        Assert.Equal("None", starting.Document.RootElement.GetProperty("agentInvocationContext").GetString());
        Assert.Equal(JsonValueKind.Null, starting.Document.RootElement.GetProperty("agentModel").ValueKind);
        Assert.Equal(JsonValueKind.Null, starting.Document.RootElement.GetProperty("agentReasoningEffort").ValueKind);
        starting.Document.Dispose();
    }

    [Fact]
    public async Task Task_details_expose_persisted_operator_audit_history()
    {
        await SeedRetryAndFallbackExecutionAsync();
        await using var c = await dataSource!.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
            VALUES(@taskId,'Implementing','Implementing','Operator stopped further automatic attempts after current execution','operator')
            """, new { taskId });

        var response = await client.GetAsync($"/api/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var events = document.RootElement.GetProperty("taskEvents");
        Assert.Contains(events.EnumerateArray(), item => item.GetProperty("actor").GetString() == "operator"
            && item.GetProperty("reason").GetString()!.Contains("stopped further automatic attempts"));
    }

    [Fact]
    public async Task Operator_answer_cites_the_recorded_retry_reason()
    {
        await SeedRetryAndFallbackExecutionAsync();
        await using var c = await dataSource!.OpenConnectionAsync();
        await c.ExecuteAsync("""
            INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
            VALUES(@taskId,'Failed','Pending','Validation failed; retry approved','operator')
            """, new { taskId });

        var response = await client.PostAsJsonAsync("/api/operator/ask", new { text = $"Why did task {taskId} retry?" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("Validation failed; retry approved", document.RootElement.GetProperty("observed").GetString());
        Assert.Equal($"/tasks/{taskId}", document.RootElement.GetProperty("evidence")[0].GetProperty("href").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("action").ValueKind);
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
            INSERT INTO factory.agent_run(id,task_id,run_id,step_id,agent,started_at,completed_at,status,attempt_number,purpose,model,reasoning_effort)
            VALUES(@id,@taskId,@runId,@stepId,'Codex-Sol',@startedAt,@completedAt,'Failed',1,'Implement','older-model','low')
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
