using System.Diagnostics;
using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
using Serilog;

// The agent actually attributable to a task: whoever is invoking it right now (current_agent, set for the
// duration of a live invocation, including after a fallback away from the task's own preference), else whoever
// last actually ran it (the most recent factory.agent_run row), else the task's own preference for one that
// hasn't run yet, else the configuration default. Never just the preferred agent, which a fallback can disagree
// with (SF-609).
const string TaskAgentExpr = """
    COALESCE(t.current_agent,(SELECT ar.agent FROM factory.agent_run ar WHERE ar.task_id=t.id ORDER BY ar.started_at DESC LIMIT 1),t.preferred_agent,'Codex')
    """;

const string TaskListSql = $"""
    SELECT t.id,t.title,gr.owner || '/' || gr.name AS repository,i.issue_number AS "issueNumber",t.status,t.priority,
      {TaskAgentExpr} AS agent,t.created_at AS "createdAt",t.started_at AS "startedAt",
      t.completed_at AS "completedAt",t.branch_name AS "branchName",t.worktree_path AS "worktreePath",t.failure_reason AS "failureReason",
      CASE WHEN t.failure_reason IS NOT NULL THEN t.failure_reason
           WHEN t.status IN ('Completed','ReadyForPublish','Published') THEN 'Passed'
           WHEN t.status='Rejected' THEN 'Pull request closed without merge'
           WHEN t.status='Cancelled' THEN 'Cancelled' END AS result,
      EXTRACT(EPOCH FROM (COALESCE(t.completed_at,now())-COALESCE(t.started_at,t.created_at))) AS "durationSeconds"
    FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id LEFT JOIN github.issue i ON i.id=t.github_issue_id
    """;

// "Busy" (ActiveTask) is read from current_agent, the live selected-at-invocation-start signal (SF-609) — never
// the task's preferred agent, which a fallback run can disagree with.
const string AgentStatsSql = """
    SELECT
      (SELECT t.title FROM factory.task t WHERE t.current_agent=@agent ORDER BY t.started_at DESC LIMIT 1) AS "ActiveTask",
      (SELECT count(*) FROM factory.agent_run WHERE agent=@agent AND started_at >= CURRENT_DATE) AS "RunsToday",
      (SELECT count(*) FROM factory.agent_run WHERE agent=@agent AND status='Succeeded') AS "SuccessfulRuns",
      (SELECT started_at FROM factory.agent_run WHERE agent=@agent AND quota_detected=true ORDER BY started_at DESC LIMIT 1) AS "QuotaDetectedAt"
    """;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((_, configuration) => configuration
    .MinimumLevel.Override("Microsoft.AspNetCore.Mvc.Infrastructure.DefaultActionDescriptorCollectionProvider", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("logs/api-.log", rollingInterval: RollingInterval.Day));
builder.Services.AddFactoryTelemetry(builder.Configuration, "Factory.Api");
builder.Services.AddFactoryInfrastructure(builder.Configuration);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton(sp => new NpgsqlDataSourceBuilder(sp.GetRequiredService<IOptions<FactoryOptions>>().Value.ConnectionString).Build());

var app = builder.Build();
app.UseCors();
if (!app.Environment.IsEnvironment("Testing"))
    await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);

var dashboardUrl = builder.Configuration["Dashboard:Url"] ?? "http://localhost:3000";
app.MapGet("/", () => Results.Ok(new
{
    service = "Software Factory API",
    status = "running",
    health = "/health",
    dashboard = dashboardUrl,
    message = "The dashboard is a separate Next.js application. Start Factory.Web, then open the dashboard URL."
}));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/dashboard", async (NpgsqlDataSource db, IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, ITaskStore tasks, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var metrics = await c.QuerySingleAsync<DashboardMetricsRow>(new CommandDefinition("""
        SELECT
          count(*) FILTER (WHERE status IN ('Claimed','Preparing','Implementing','Validating','Reviewing')) AS "ActiveTasks",
          count(*) FILTER (WHERE status='Pending') AS "PendingTasks",
          count(*) FILTER (WHERE status='Completed' AND completed_at >= CURRENT_DATE) AS "CompletedToday",
          count(*) FILTER (WHERE status='NeedsHuman') AS "NeedsOperator",
          COALESCE(round(100.0 * count(*) FILTER (WHERE status='Completed') / NULLIF(count(*) FILTER (WHERE status IN ('Completed','Failed','Rejected')),0),1),0) AS "SuccessRate"
        FROM factory.task
        """, cancellationToken: ct));
    var active = await c.QueryAsync(new CommandDefinition(TaskListSql + " WHERE t.status IN ('Claimed','Preparing','Implementing','Validating','Reviewing') ORDER BY t.started_at DESC LIMIT 8", cancellationToken: ct));
    var activity = await c.QueryAsync(new CommandDefinition("SELECT s.step_type AS type,s.status,s.completed_at AS \"occurredAt\",t.title FROM factory.step s JOIN factory.run r ON r.id=s.run_id JOIN factory.task t ON t.id=r.task_id WHERE s.completed_at IS NOT NULL ORDER BY s.completed_at DESC LIMIT 12", cancellationToken: ct));
    var throughput = await c.QueryAsync(new CommandDefinition("SELECT d::date AS day,count(t.id) AS completed FROM generate_series(CURRENT_DATE-6,CURRENT_DATE,'1 day') d LEFT JOIN factory.task t ON t.completed_at::date=d::date GROUP BY d ORDER BY d", cancellationToken: ct));
    var agentStatus = await ComputeAgentStatusAsync(c, availabilityCheckers, tasks, ct);

    // "Why is nothing running right now" — answered from the same evidence already gathered above, so the
    // operator never has to cross-reference the pending count against the agent table by hand.
    string? idleReason = metrics.ActiveTasks > 0 ? null
        : metrics.PendingTasks == 0 ? "No pending tasks queued."
        : agentStatus.Count > 0 && agentStatus.All(a => a.State is "QuotaBlocked" or "Unavailable" or "Unknown")
            ? "No configured agent is currently available to claim work."
        : "Waiting to claim the next pending task.";

    return Results.Ok(new { metrics, active, activity, throughput, agentStatus, idleReason });
});

app.MapGet("/api/agents/status", async (NpgsqlDataSource db, IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, ITaskStore tasks, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    return Results.Ok(await ComputeAgentStatusAsync(c, availabilityCheckers, tasks, ct));
});

// Durable pause/resume (SF-610). Pausing stops new dispatch only — a task already claimed and executing always
// finishes, and publication of already-validated work is untouched, since it consumes no agent's subscription.
app.MapGet("/api/control/pause", async (ITaskStore tasks, CancellationToken ct) =>
    Results.Ok(await tasks.GetAllDispatchPausesAsync(ct)));

app.MapPost("/api/control/pause", async (PauseRequest? body, ITaskStore tasks, CancellationToken ct) =>
{
    await tasks.SetDispatchPauseAsync(DispatchPauseScope.Global, true, body?.Reason, "operator", ct);
    return Results.NoContent();
});

app.MapPost("/api/control/resume", async (ITaskStore tasks, CancellationToken ct) =>
{
    await tasks.SetDispatchPauseAsync(DispatchPauseScope.Global, false, null, "operator", ct);
    return Results.NoContent();
});

app.MapPost("/api/agents/{agent}/pause", async (string agent, PauseRequest? body, ITaskStore tasks, CancellationToken ct) =>
{
    if (agent == DispatchPauseScope.Global) return Results.BadRequest(new { error = "Use /api/control/pause to pause the whole factory." });
    await tasks.SetDispatchPauseAsync(agent, true, body?.Reason, "operator", ct);
    return Results.NoContent();
});

app.MapPost("/api/agents/{agent}/resume", async (string agent, ITaskStore tasks, CancellationToken ct) =>
{
    await tasks.SetDispatchPauseAsync(agent, false, null, "operator", ct);
    return Results.NoContent();
});

app.MapGet("/api/tasks", async (string? status, string? repository, string? agent, string? q, string? sort, string? direction, int? page, int? pageSize, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var filters = new List<string>();
    if (!string.IsNullOrWhiteSpace(status)) filters.Add("t.status=@status");
    if (!string.IsNullOrWhiteSpace(repository)) filters.Add("(gr.owner || '/' || gr.name)=@repository");
    if (!string.IsNullOrWhiteSpace(agent)) filters.Add($"{TaskAgentExpr}=@agent");
    if (!string.IsNullOrWhiteSpace(q)) filters.Add($"(t.title ILIKE '%' || @q || '%' OR (gr.owner || '/' || gr.name) ILIKE '%' || @q || '%' OR {TaskAgentExpr} ILIKE '%' || @q || '%' OR i.issue_number::text ILIKE '%' || @q || '%')");
    var where = filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
    var query = TaskListQuery.Normalize(page, pageSize, sort, direction);
    var countSql = "SELECT count(*) FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id LEFT JOIN github.issue i ON i.id=t.github_issue_id" + where;
    var total = await c.ExecuteScalarAsync<int>(new CommandDefinition(countSql, new { status, repository, agent, q }, cancellationToken: ct));
    var normalizedPage = Math.Min(query.Page, Math.Max(1, (int)Math.Ceiling(total / (double)query.Size)));
    var offset = (normalizedPage - 1) * query.Size;
    var items = await c.QueryAsync(new CommandDefinition(TaskListSql + where + $" ORDER BY {query.SortExpression} {query.Direction}, t.id LIMIT @Size OFFSET @Offset", new { status, repository, agent, q, query.Size, Offset = offset }, cancellationToken: ct));
    return Results.Ok(new { items, total, page = normalizedPage, pageSize = query.Size });
});

app.MapGet("/api/tasks/{id:guid}", async (Guid id, NpgsqlDataSource db, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.get_task");
    activity?.SetTag("factory.task_id", id);
    await using var c = await db.OpenConnectionAsync(ct);
    var task = await c.QuerySingleOrDefaultAsync(new CommandDefinition(TaskListSql + " WHERE t.id=@id", new { id }, cancellationToken: ct));
    if (task is null) return Results.NotFound();
    var dependencies = await tasks.GetDependenciesAsync(id, ct);
    var issue = await c.QuerySingleOrDefaultAsync(new CommandDefinition("SELECT i.issue_number AS \"issueNumber\",i.title,i.body,i.state,i.author,i.created_at AS \"createdAt\",array_agg(l.name) FILTER (WHERE l.name IS NOT NULL) AS labels FROM github.issue i LEFT JOIN github.issue_label l ON l.issue_id=i.id JOIN factory.task t ON t.github_issue_id=i.id WHERE t.id=@id GROUP BY i.id", new { id }, cancellationToken: ct));
    var comments = await c.QueryAsync(new CommandDefinition("SELECT c.github_comment_id AS \"githubCommentId\",c.author,c.body,c.created_at AS \"createdAt\",c.updated_at AS \"updatedAt\" FROM github.issue_comment c JOIN factory.task t ON t.github_issue_id=c.issue_id WHERE t.id=@id ORDER BY c.created_at", new { id }, cancellationToken: ct));
    var runs = await c.QueryAsync(new CommandDefinition("""
        SELECT id,started_at AS "startedAt",completed_at AS "completedAt",status,worker_id AS "workerId",
          base_commit AS "baseCommit",head_commit AS "headCommit",files_changed AS "filesChanged",lines_added AS "linesAdded",lines_removed AS "linesRemoved"
        FROM factory.run WHERE task_id=@id ORDER BY started_at DESC
        """, new { id }, cancellationToken: ct));
    var steps = await c.QueryAsync(new CommandDefinition("SELECT s.id,s.run_id AS \"runId\",s.step_type AS \"stepType\",s.status,s.started_at AS \"startedAt\",s.completed_at AS \"completedAt\",s.duration_ms AS \"durationMs\",s.attempt,s.error,s.output,(s.log_path IS NOT NULL) AS \"hasLog\",(coalesce(length(s.output),0)>=65536) AS \"outputTruncated\" FROM factory.step s JOIN factory.run r ON r.id=s.run_id WHERE r.task_id=@id ORDER BY s.started_at", new { id }, cancellationToken: ct));
    var agentRunRows = await c.QueryAsync<AgentRunDetailsRow>(new CommandDefinition("""
        SELECT id,run_id AS "RunId",agent,started_at AS "StartedAt",completed_at AS "CompletedAt",duration_seconds AS "DurationSeconds",
          exit_code AS "ExitCode",status,stdout,stderr,quota_detected AS "QuotaDetected",attempt_number AS "AttemptNumber",needs_human AS "NeedsHuman",
          result_json::text AS "ResultJson",result_summary AS "ResultSummary",tests_run::text AS "TestsRunJson",tests_passed AS "TestsPassed",
          files_changed::text AS "FilesChangedJson",risks::text AS "RisksJson",human_reason AS "HumanReason"
        FROM factory.agent_run WHERE task_id=@id ORDER BY started_at
        """, new { id }, cancellationToken: ct));
    var agentRuns = agentRunRows.Select(AgentRunDetailsMapper.Map);
    var publications = await c.QueryAsync(new CommandDefinition("""
        SELECT id,status,requested_at AS "requestedAt",requested_by AS "requestedBy",completed_at AS "completedAt",
          pull_request_number AS "pullRequestNumber",pull_request_url AS "pullRequestUrl",error
        FROM factory.publication WHERE task_id=@id ORDER BY requested_at DESC
        """, new { id }, cancellationToken: ct));
    var dependencyDtos = dependencies.Select(d => new { d.TaskId, d.DependsOnTaskId, d.DependsOnTitle, DependsOnStatus = d.DependsOnStatus.ToString() });
    return Results.Ok(new { task, issue, comments, runs, steps, agentRuns, publications, dependencies = dependencyDtos });
});

app.MapPost("/api/tasks/{id:guid}/retry", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.retry_task");
    activity?.SetTag("factory.task_id", id);
    return await tasks.RetryAsync(id, ct) ? Results.Accepted($"/api/tasks/{id}") : Results.Conflict(new { error = "Task cannot be retried from its current state." });
});

app.MapPost("/api/tasks/{id:guid}/cancel", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.cancel_task");
    activity?.SetTag("factory.task_id", id);
    return await tasks.CancelAsync(id, ct) ? Results.NoContent() : Results.Conflict(new { error = "Task cannot be cancelled." });
});

app.MapPost("/api/tasks/{id:guid}/priority", async (Guid id, PriorityRequest body, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.set_task_priority");
    activity?.SetTag("factory.task_id", id);
    await tasks.SetPriorityAsync(id, body.Priority, ct);
    return Results.NoContent();
});

app.MapPost("/api/tasks/{id:guid}/dependencies", async (Guid id, DependencyRequest body, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.add_task_dependency");
    activity?.SetTag("factory.task_id", id);
    var outcome = await tasks.AddDependencyAsync(id, body.DependsOnTaskId, ct);
    return outcome switch
    {
        AddDependencyOutcome.Added or AddDependencyOutcome.AlreadyExists => Results.NoContent(),
        AddDependencyOutcome.WouldCreateCycle => Results.Conflict(new { error = "Adding this dependency would create a cycle." }),
        AddDependencyOutcome.SelfDependency => Results.Conflict(new { error = "A task cannot depend on itself." }),
        AddDependencyOutcome.TaskNotFound => Results.NotFound(new { error = "One of these tasks does not exist." }),
        _ => Results.Problem()
    };
});

app.MapDelete("/api/tasks/{id:guid}/dependencies/{dependsOnId:guid}", async (Guid id, Guid dependsOnId, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.remove_task_dependency");
    activity?.SetTag("factory.task_id", id);
    await tasks.RemoveDependencyAsync(id, dependsOnId, ct);
    return Results.NoContent();
});

app.MapPost("/api/tasks/{id:guid}/publish", async (Guid id, ITaskStore tasks, NpgsqlDataSource db, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.publish_task");
    activity?.SetTag("factory.task_id", id);
    await using var c = await db.OpenConnectionAsync(ct);
    var status = await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT status FROM factory.task WHERE id=@id", new { id }, cancellationToken: ct));
    if (status is null) return Results.NotFound();
    if (status != "ReadyForPublish") return Results.Conflict(new { error = "Task is not ready for publish." });
    var publicationId = await tasks.RequestPublicationAsync(id, null, "operator", ct);
    return publicationId is null
        ? Results.Conflict(new { error = "A publication attempt is already in progress for this task." })
        : Results.Accepted($"/api/tasks/{id}");
});

app.MapGet("/api/issues", async (string? repository, string? state, bool? eligible, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var filters = new List<string>();
    if (!string.IsNullOrWhiteSpace(repository)) filters.Add("(r.owner || '/' || r.name)=@repository");
    if (!string.IsNullOrWhiteSpace(state)) filters.Add("i.state=@state");
    if (eligible is not null) filters.Add(eligible.Value
        ? "EXISTS(SELECT 1 FROM github.issue_label eligibility WHERE eligibility.issue_id=i.id AND lower(eligibility.name)='factory:ready')"
        : "NOT EXISTS(SELECT 1 FROM github.issue_label eligibility WHERE eligibility.issue_id=i.id AND lower(eligibility.name)='factory:ready')");
    var where = filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
    var items = await c.QueryAsync(new CommandDefinition("""
        SELECT i.id,i.issue_number AS "issueNumber",i.title,i.state,i.author,i.created_at AS "createdAt",i.updated_at AS "updatedAt",
          r.owner || '/' || r.name AS repository,
          array_agg(label.name ORDER BY label.name) FILTER (WHERE label.name IS NOT NULL) AS labels,
          EXISTS(SELECT 1 FROM github.issue_label eligibility WHERE eligibility.issue_id=i.id AND lower(eligibility.name)='factory:ready') AS eligible,
          count(DISTINCT t.id) AS "taskCount"
        FROM github.issue i
        JOIN github.repository r ON r.id=i.repository_id
        LEFT JOIN github.issue_label label ON label.issue_id=i.id
        LEFT JOIN factory.task t ON t.github_issue_id=i.id
        """ + where + " GROUP BY i.id,r.owner,r.name ORDER BY i.updated_at DESC,i.id DESC", new { repository, state, eligible }, cancellationToken: ct));
    return Results.Ok(items);
});
app.MapGet("/api/issues/{id:long}", async (long id, NpgsqlDataSource db, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.get_issue");
    activity?.SetTag("factory.issue_id", id);
    await using var c = await db.OpenConnectionAsync(ct);
    var issue = await c.QuerySingleOrDefaultAsync(new CommandDefinition("""
        SELECT i.id,i.issue_number AS "issueNumber",i.title,i.body,i.state,i.author,i.created_at AS "createdAt",i.updated_at AS "updatedAt",i.closed_at AS "closedAt",
          r.owner || '/' || r.name AS repository,
          array_agg(label.name ORDER BY label.name) FILTER (WHERE label.name IS NOT NULL) AS labels,
          EXISTS(SELECT 1 FROM github.issue_label eligibility WHERE eligibility.issue_id=i.id AND lower(eligibility.name)='factory:ready') AS eligible
        FROM github.issue i JOIN github.repository r ON r.id=i.repository_id
        LEFT JOIN github.issue_label label ON label.issue_id=i.id
        WHERE i.id=@id GROUP BY i.id,r.owner,r.name
        """, new { id }, cancellationToken: ct));
    if (issue is null) return Results.NotFound();
    var comments = await c.QueryAsync(new CommandDefinition("SELECT github_comment_id AS \"githubCommentId\",author,body,created_at AS \"createdAt\",updated_at AS \"updatedAt\" FROM github.issue_comment WHERE issue_id=@id ORDER BY created_at,id", new { id }, cancellationToken: ct));
    var tasks = await c.QueryAsync(new CommandDefinition($"""
        SELECT t.id,t.title,t.status,{TaskAgentExpr} AS agent,t.created_at AS "createdAt",t.started_at AS "startedAt",t.completed_at AS "completedAt",
          t.failure_reason AS "failureReason",t.branch_name AS "branchName"
        FROM factory.task t WHERE t.github_issue_id=@id ORDER BY t.created_at DESC,t.id
        """, new { id }, cancellationToken: ct));
    return Results.Ok(new { issue, comments, tasks });
});

app.MapGet("/api/runs", async (string? status, string? worker, string? repository, DateOnly? from, DateOnly? to, int? page, int? pageSize, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var filters = new List<string>();
    if (!string.IsNullOrWhiteSpace(status)) filters.Add("r.status=@status");
    if (!string.IsNullOrWhiteSpace(worker)) filters.Add("r.worker_id=@worker");
    if (!string.IsNullOrWhiteSpace(repository)) filters.Add("(gr.owner || '/' || gr.name)=@repository");
    var fromInstant = from?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    var toExclusive = to?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    if (fromInstant is not null) filters.Add("r.started_at >= @fromInstant");
    if (toExclusive is not null) filters.Add("r.started_at < @toExclusive");
    var where = filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
    var query = RunListQuery.Normalize(page, pageSize);
    var parameters = new { status, worker, repository, fromInstant, toExclusive, query.Size };
    var total = await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*) FROM factory.run r JOIN factory.task t ON t.id=r.task_id JOIN github.repository gr ON gr.id=t.repository_id" + where, parameters, cancellationToken: ct));
    var normalizedPage = Math.Min(query.Page, Math.Max(1, (int)Math.Ceiling(total / (double)query.Size)));
    var offset = (normalizedPage - 1) * query.Size;
    var items = await c.QueryAsync(new CommandDefinition("""
        SELECT r.id,r.task_id AS "taskId",t.title,gr.owner || '/' || gr.name AS repository,
          r.started_at AS "startedAt",r.completed_at AS "completedAt",r.status,r.worker_id AS "workerId",
          EXTRACT(EPOCH FROM (COALESCE(r.completed_at,now())-r.started_at)) AS "durationSeconds",
          latest.step_type AS "currentStep",
          CASE WHEN r.status='Succeeded' THEN 'Passed'
               WHEN r.status='Failed' THEN COALESCE(latest.error,t.failure_reason,'Failed')
               WHEN r.status='Running' THEN NULL ELSE r.status END AS result
        FROM factory.run r
        JOIN factory.task t ON t.id=r.task_id
        JOIN github.repository gr ON gr.id=t.repository_id
        LEFT JOIN LATERAL (
          SELECT s.step_type,s.error FROM factory.step s WHERE s.run_id=r.id
          ORDER BY (s.status='Running') DESC,s.started_at DESC LIMIT 1
        ) latest ON TRUE
        """ + where + " ORDER BY r.started_at DESC,r.id LIMIT @Size OFFSET @Offset", new { status, worker, repository, fromInstant, toExclusive, query.Size, Offset = offset }, cancellationToken: ct));
    return Results.Ok(new { items, total, page = normalizedPage, pageSize = query.Size });
});
app.MapGet("/api/runs/workers", Query("SELECT DISTINCT worker_id AS worker FROM factory.run ORDER BY worker_id"));
app.MapGet("/api/runs/{id:guid}", async (Guid id, NpgsqlDataSource db, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.get_run");
    activity?.SetTag("factory.run_id", id);
    await using var c = await db.OpenConnectionAsync(ct);
    var run = await c.QuerySingleOrDefaultAsync(new CommandDefinition("""
        SELECT r.id,r.task_id AS "taskId",t.title,gr.owner || '/' || gr.name AS repository,
          r.started_at AS "startedAt",r.completed_at AS "completedAt",r.status,r.worker_id AS "workerId",
          EXTRACT(EPOCH FROM (COALESCE(r.completed_at,now())-r.started_at)) AS "durationSeconds",
          latest.step_type AS "currentStep",
          CASE WHEN r.status='Succeeded' THEN 'Passed'
               WHEN r.status='Failed' THEN COALESCE(latest.error,t.failure_reason,'Failed')
               WHEN r.status='Running' THEN NULL ELSE r.status END AS result,
          r.base_commit AS "baseCommit",r.head_commit AS "headCommit",r.files_changed AS "filesChanged",r.lines_added AS "linesAdded",r.lines_removed AS "linesRemoved"
        FROM factory.run r
        JOIN factory.task t ON t.id=r.task_id
        JOIN github.repository gr ON gr.id=t.repository_id
        LEFT JOIN LATERAL (
          SELECT s.step_type,s.error FROM factory.step s WHERE s.run_id=r.id
          ORDER BY (s.status='Running') DESC,s.started_at DESC LIMIT 1
        ) latest ON TRUE
        WHERE r.id=@id
        """, new { id }, cancellationToken: ct));
    if (run is null) return Results.NotFound();
    var steps = await c.QueryAsync(new CommandDefinition("SELECT id,run_id AS \"runId\",step_type AS \"stepType\",status,started_at AS \"startedAt\",completed_at AS \"completedAt\",duration_ms AS \"durationMs\",attempt,error,output,(log_path IS NOT NULL) AS \"hasLog\",(coalesce(length(output),0)>=65536) AS \"outputTruncated\" FROM factory.step WHERE run_id=@id ORDER BY started_at,id", new { id }, cancellationToken: ct));
    var agentRunRows = await c.QueryAsync<AgentRunDetailsRow>(new CommandDefinition("""
        SELECT id,run_id AS "RunId",agent,started_at AS "StartedAt",completed_at AS "CompletedAt",duration_seconds AS "DurationSeconds",
          exit_code AS "ExitCode",status,stdout,stderr,quota_detected AS "QuotaDetected",attempt_number AS "AttemptNumber",needs_human AS "NeedsHuman",
          result_json::text AS "ResultJson",result_summary AS "ResultSummary",tests_run::text AS "TestsRunJson",tests_passed AS "TestsPassed",
          files_changed::text AS "FilesChangedJson",risks::text AS "RisksJson",human_reason AS "HumanReason"
        FROM factory.agent_run WHERE run_id=@id ORDER BY started_at,id
        """, new { id }, cancellationToken: ct));
    return Results.Ok(new { run, steps, agentRuns = agentRunRows.Select(AgentRunDetailsMapper.Map) });
});

app.MapGet("/api/steps/{id:guid}/log", async (Guid id, bool? tail, NpgsqlDataSource db, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.get_step_log");
    activity?.SetTag("factory.step_id", id);
    await using var c = await db.OpenConnectionAsync(ct);
    var logPath = await c.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT log_path FROM factory.step WHERE id=@id", new { id }, cancellationToken: ct));
    if (logPath is null) return Results.NotFound(new { error = "No log was recorded for this step." });
    if (!File.Exists(logPath)) return Results.NotFound(new { error = "The log file is not available on disk (it may not have been written yet, or this API instance does not share the orchestrator's log directory)." });

    if (tail != true) return Results.File(logPath, "text/plain; charset=utf-8", enableRangeProcessing: true);

    const int tailBytes = 64 * 1024;
    await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    var start = Math.Max(0, stream.Length - tailBytes);
    stream.Seek(start, SeekOrigin.Begin);
    using var reader = new StreamReader(stream);
    return Results.Text(await reader.ReadToEndAsync(ct), "text/plain", System.Text.Encoding.UTF8);
});

app.MapGet("/api/repositories", Query("""
    SELECT r.id,r.owner,r.name,r.clone_url AS "cloneUrl",r.default_branch AS "defaultBranch",r.is_enabled AS "isEnabled",r.last_synced_at AS "lastSyncedAt",
      failure.error AS "latestSyncFailure",failure.occurred_at AS "latestSyncFailureAt"
    FROM github.repository r
    LEFT JOIN LATERAL (
      SELECT error,occurred_at FROM github.repository_sync_failure WHERE repository_id=r.id ORDER BY occurred_at DESC,id DESC LIMIT 1
    ) failure ON TRUE
    ORDER BY r.owner,r.name
    """));
app.MapGet("/api/repositories/{id:long}", async (long id, NpgsqlDataSource db, IRepositoryConfigurationReader configurationReader, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.get_repository");
    activity?.SetTag("factory.repository_id", id);
    await using var c = await db.OpenConnectionAsync(ct);
    var item = await c.QuerySingleOrDefaultAsync(new CommandDefinition("""
        SELECT r.id,r.owner,r.name,r.clone_url AS "cloneUrl",r.default_branch AS "defaultBranch",r.is_enabled AS "isEnabled",
          r.created_at AS "createdAt",r.updated_at AS "updatedAt",r.last_synced_at AS "lastSyncedAt",
          failure.error AS "latestSyncFailure",failure.occurred_at AS "latestSyncFailureAt",
          (SELECT count(*) FROM github.issue i WHERE i.repository_id=r.id) AS "issueCount",
          (SELECT count(*) FROM factory.task t WHERE t.repository_id=r.id) AS "taskCount",
          (SELECT worktree_path FROM factory.task t WHERE t.repository_id=r.id AND t.worktree_path IS NOT NULL ORDER BY t.created_at DESC LIMIT 1) AS "configurationWorktreePath"
        FROM github.repository r
        LEFT JOIN LATERAL (
          SELECT error,occurred_at FROM github.repository_sync_failure WHERE repository_id=r.id ORDER BY occurred_at DESC,id DESC LIMIT 1
        ) failure ON TRUE
        WHERE r.id=@id
        """, new { id }, cancellationToken: ct));
    if (item is null) return Results.NotFound();

    var worktreePath = (string?)item.configurationWorktreePath;
    RepositoryConfiguration? configuration = null;
    if (!string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath))
        configuration = await configurationReader.ReadAsync(worktreePath, $"origin/{(string)item.defaultBranch}", ct);
    return Results.Ok(new { item.id, item.owner, item.name, item.cloneUrl, item.defaultBranch, item.isEnabled, item.createdAt, item.updatedAt, item.lastSyncedAt, item.latestSyncFailure, item.latestSyncFailureAt, item.issueCount, item.taskCount, configuration });
});
app.MapGet("/api/workers", async (NpgsqlDataSource db, IOptions<FactoryOptions> options, CancellationToken ct) =>
{
    // A worker heartbeats at least every max(PollingIntervalSeconds, LeaseHeartbeatSeconds); tripling that bound
    // before calling it stale tolerates a couple of missed cycles without flapping the dashboard.
    var staleAfterSeconds = Math.Max(options.Value.PollingIntervalSeconds, options.Value.LeaseHeartbeatSeconds) * 3;
    await using var c = await db.OpenConnectionAsync(ct);
    var workers = await c.QueryAsync(new CommandDefinition("""
        SELECT w.worker_id AS "workerId",w.host,w.last_seen_at AS "lastSeenAt",w.current_task_id AS "currentTaskId",
          t.title AS "currentTaskTitle",(now()-w.last_seen_at) > make_interval(secs => @staleAfterSeconds) AS "isStale"
        FROM factory.worker w LEFT JOIN factory.task t ON t.id=w.current_task_id
        ORDER BY w.last_seen_at DESC
        """, new { staleAfterSeconds }, cancellationToken: ct));
    return Results.Ok(workers);
});
app.MapGet("/api/agents", Query("SELECT COALESCE(preferred_agent,'Codex') AS agent,count(*) AS tasks,count(*) FILTER(WHERE status='Completed') AS successful FROM factory.task GROUP BY 1"));
app.MapGet("/api/agents/{agent}/runs", async (string agent, NpgsqlDataSource db, CancellationToken ct) => { await using var c = await db.OpenConnectionAsync(ct); return Results.Ok(await c.QueryAsync(new CommandDefinition("SELECT id,task_id AS \"taskId\",run_id AS \"runId\",agent,started_at AS \"startedAt\",completed_at AS \"completedAt\",duration_seconds AS \"durationSeconds\",exit_code AS \"exitCode\",status,quota_detected AS \"quotaDetected\",quota_reset_at AS \"quotaResetAt\",attempt_number AS \"attemptNumber\",needs_human AS \"needsHuman\" FROM factory.agent_run WHERE agent=@agent ORDER BY started_at DESC LIMIT 100", new { agent }, cancellationToken: ct))); });
app.MapGet("/api/metrics/summary", Query("SELECT count(*) AS attempted,count(*) FILTER(WHERE status='Completed') AS completed,count(*) FILTER(WHERE status='NeedsHuman') AS \"humanInterventions\" FROM factory.task"));
app.MapGet("/api/metrics/throughput", Query("SELECT completed_at::date AS day,count(*) AS completed FROM factory.task WHERE completed_at IS NOT NULL GROUP BY 1 ORDER BY 1"));
app.MapGet("/api/metrics/agents", Query("SELECT agent,count(*) AS runs,count(*) FILTER(WHERE status='Succeeded') AS successful,avg(duration_seconds) AS \"averageDurationSeconds\" FROM factory.agent_run GROUP BY agent"));

await app.RunAsync();

static Func<NpgsqlDataSource, CancellationToken, Task<IResult>> Query(string sql) => async (db, ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    return Results.Ok(await c.QueryAsync(new CommandDefinition(sql, cancellationToken: ct)));
};

// Shared by /api/dashboard and /api/agents/status so the header's compact status pill and the full Overview
// panel can never disagree about an agent's state.
static async Task<List<AgentStatus>> ComputeAgentStatusAsync(NpgsqlConnection c, IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, ITaskStore tasks, CancellationToken ct)
{
    var agentStatus = new List<AgentStatus>();
    foreach (var checker in availabilityCheckers)
    {
        bool succeeded, errored;
        string? version, error;
        try
        {
            var availability = await checker.CheckAsync(ct);
            (succeeded, errored, version, error) = (availability.Available, false, availability.Version, availability.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The check itself failed unexpectedly — a genuinely unknown state, never silently reported as
            // available or unavailable, and never allowed to fail this whole endpoint over one broken agent.
            (succeeded, errored, version, error) = (false, true, null, ex.Message);
        }

        var stats = await c.QuerySingleAsync<AgentStatsRow>(new CommandDefinition(AgentStatsSql, new { agent = checker.Agent }, cancellationToken: ct));
        var isAtQuota = await tasks.IsAgentAtQuotaAsync(checker.Agent, ct);
        var quotaStatus = await tasks.GetAgentQuotaStatusAsync(checker.Agent, ct);
        var pause = await tasks.GetDispatchPauseAsync(checker.Agent, ct);
        var state = AgentOperationalStateResolver.Resolve(succeeded, errored, pause.Paused, isAtQuota, stats.ActiveTask is not null, stats.SuccessfulRuns > 0);
        var (quotaResetAt, quotaWindow, quotaResetKind) = isAtQuota && quotaStatus is not null
            ? (quotaStatus.ResetAt, quotaStatus.Window.ToString(), quotaStatus.ResetKind.ToString())
            : (null, null, null);
        agentStatus.Add(new AgentStatus(checker.Agent, state.ToString(), version, error,
            stats.ActiveTask, stats.RunsToday, stats.SuccessfulRuns, stats.QuotaDetectedAt, quotaResetAt, quotaWindow, quotaResetKind,
            pause.Paused ? pause.Reason : null));
    }
    return agentStatus;
}

public partial class Program;
