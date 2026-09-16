using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Trace;
using Serilog;

const string TaskListSql = """
    SELECT t.id,t.title,gr.owner || '/' || gr.name AS repository,i.issue_number AS "issueNumber",t.status,
      COALESCE(t.preferred_agent,'Codex') AS agent,t.created_at AS "createdAt",t.started_at AS "startedAt",
      t.completed_at AS "completedAt",t.branch_name AS "branchName",t.worktree_path AS "worktreePath",t.failure_reason AS "failureReason",
      CASE WHEN t.failure_reason IS NOT NULL THEN t.failure_reason
           WHEN t.status IN ('Completed','ReadyForPublish') THEN 'Passed'
           WHEN t.status='Cancelled' THEN 'Cancelled' END AS result,
      EXTRACT(EPOCH FROM (COALESCE(t.completed_at,now())-COALESCE(t.started_at,t.created_at))) AS "durationSeconds"
    FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id LEFT JOIN github.issue i ON i.id=t.github_issue_id
    """;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((_, configuration) => configuration
    .MinimumLevel.Override("Microsoft.AspNetCore.Mvc.Infrastructure.DefaultActionDescriptorCollectionProvider", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File("logs/api-.log", rollingInterval: RollingInterval.Day));
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource("Factory.Api"));
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

app.MapGet("/api/dashboard", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var metrics = await c.QuerySingleAsync(new CommandDefinition("""
        SELECT
          count(*) FILTER (WHERE status IN ('Claimed','Preparing','Implementing','Validating','Reviewing')) AS "activeTasks",
          count(*) FILTER (WHERE status='Pending') AS "pendingTasks",
          count(*) FILTER (WHERE status='Completed' AND completed_at >= CURRENT_DATE) AS "completedToday",
          COALESCE(round(100.0 * count(*) FILTER (WHERE status='Completed') / NULLIF(count(*) FILTER (WHERE status IN ('Completed','Failed')),0),1),0) AS "successRate"
        FROM factory.task
        """, cancellationToken: ct));
    var active = await c.QueryAsync(new CommandDefinition(TaskListSql + " WHERE t.status IN ('Claimed','Preparing','Implementing','Validating','Reviewing') ORDER BY t.started_at DESC LIMIT 8", cancellationToken: ct));
    var activity = await c.QueryAsync(new CommandDefinition("SELECT s.step_type AS type,s.status,s.completed_at AS \"occurredAt\",t.title FROM factory.step s JOIN factory.run r ON r.id=s.run_id JOIN factory.task t ON t.id=r.task_id WHERE s.completed_at IS NOT NULL ORDER BY s.completed_at DESC LIMIT 12", cancellationToken: ct));
    var throughput = await c.QueryAsync(new CommandDefinition("SELECT d::date AS day,count(t.id) AS completed FROM generate_series(CURRENT_DATE-6,CURRENT_DATE,'1 day') d LEFT JOIN factory.task t ON t.completed_at::date=d::date GROUP BY d ORDER BY d", cancellationToken: ct));
    return Results.Ok(new { metrics, active, activity, throughput });
});

app.MapGet("/api/tasks", async (string? status, string? repository, string? agent, string? q, string? sort, string? direction, int? page, int? pageSize, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var filters = new List<string>();
    if (!string.IsNullOrWhiteSpace(status)) filters.Add("t.status=@status");
    if (!string.IsNullOrWhiteSpace(repository)) filters.Add("(gr.owner || '/' || gr.name)=@repository");
    if (!string.IsNullOrWhiteSpace(agent)) filters.Add("COALESCE(t.preferred_agent,'Codex')=@agent");
    if (!string.IsNullOrWhiteSpace(q)) filters.Add("(t.title ILIKE '%' || @q || '%' OR (gr.owner || '/' || gr.name) ILIKE '%' || @q || '%' OR COALESCE(t.preferred_agent,'Codex') ILIKE '%' || @q || '%' OR i.issue_number::text ILIKE '%' || @q || '%')");
    var where = filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
    var query = TaskListQuery.Normalize(page, pageSize, sort, direction);
    var countSql = "SELECT count(*) FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id LEFT JOIN github.issue i ON i.id=t.github_issue_id" + where;
    var total = await c.ExecuteScalarAsync<int>(new CommandDefinition(countSql, new { status, repository, agent, q }, cancellationToken: ct));
    var normalizedPage = Math.Min(query.Page, Math.Max(1, (int)Math.Ceiling(total / (double)query.Size)));
    var offset = (normalizedPage - 1) * query.Size;
    var items = await c.QueryAsync(new CommandDefinition(TaskListSql + where + $" ORDER BY {query.SortExpression} {query.Direction}, t.id LIMIT @Size OFFSET @Offset", new { status, repository, agent, q, query.Size, Offset = offset }, cancellationToken: ct));
    return Results.Ok(new { items, total, page = normalizedPage, pageSize = query.Size });
});

app.MapGet("/api/tasks/{id:guid}", async (Guid id, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var task = await c.QuerySingleOrDefaultAsync(new CommandDefinition(TaskListSql + " WHERE t.id=@id", new { id }, cancellationToken: ct));
    if (task is null) return Results.NotFound();
    var issue = await c.QuerySingleOrDefaultAsync(new CommandDefinition("SELECT i.issue_number AS \"issueNumber\",i.title,i.body,i.state,i.author,i.created_at AS \"createdAt\",array_agg(l.name) FILTER (WHERE l.name IS NOT NULL) AS labels FROM github.issue i LEFT JOIN github.issue_label l ON l.issue_id=i.id JOIN factory.task t ON t.github_issue_id=i.id WHERE t.id=@id GROUP BY i.id", new { id }, cancellationToken: ct));
    var runs = await c.QueryAsync(new CommandDefinition("SELECT id,started_at AS \"startedAt\",completed_at AS \"completedAt\",status,worker_id AS \"workerId\" FROM factory.run WHERE task_id=@id ORDER BY started_at DESC", new { id }, cancellationToken: ct));
    var steps = await c.QueryAsync(new CommandDefinition("SELECT s.id,s.run_id AS \"runId\",s.step_type AS \"stepType\",s.status,s.started_at AS \"startedAt\",s.completed_at AS \"completedAt\",s.duration_ms AS \"durationMs\",s.attempt,s.error,s.output FROM factory.step s JOIN factory.run r ON r.id=s.run_id WHERE r.task_id=@id ORDER BY s.started_at", new { id }, cancellationToken: ct));
    var agentRuns = await c.QueryAsync(new CommandDefinition("SELECT id,run_id AS \"runId\",agent,started_at AS \"startedAt\",completed_at AS \"completedAt\",duration_seconds AS \"durationSeconds\",exit_code AS \"exitCode\",status,stdout,stderr,quota_detected AS \"quotaDetected\",attempt_number AS \"attemptNumber\",needs_human AS \"needsHuman\" FROM factory.agent_run WHERE task_id=@id ORDER BY started_at", new { id }, cancellationToken: ct));
    return Results.Ok(new { task, issue, runs, steps, agentRuns });
});

app.MapPost("/api/tasks/{id:guid}/retry", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
    await tasks.RetryAsync(id, ct) ? Results.Accepted($"/api/tasks/{id}") : Results.Conflict(new { error = "Task cannot be retried from its current state." }));

app.MapPost("/api/tasks/{id:guid}/cancel", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
    await tasks.CancelAsync(id, ct) ? Results.NoContent() : Results.Conflict(new { error = "Task cannot be cancelled." }));

app.MapGet("/api/runs", Query("SELECT r.id,r.task_id AS \"taskId\",t.title,r.started_at AS \"startedAt\",r.completed_at AS \"completedAt\",r.status,r.worker_id AS \"workerId\" FROM factory.run r JOIN factory.task t ON t.id=r.task_id ORDER BY r.started_at DESC LIMIT 100"));
app.MapGet("/api/runs/{id:guid}", async (Guid id, NpgsqlDataSource db, CancellationToken ct) => { await using var c = await db.OpenConnectionAsync(ct); return Results.Ok(await c.QueryAsync(new CommandDefinition("SELECT id,run_id AS \"runId\",step_type AS \"stepType\",status,started_at AS \"startedAt\",completed_at AS \"completedAt\",duration_ms AS \"durationMs\",attempt,error,output FROM factory.step WHERE run_id=@id ORDER BY started_at", new { id }, cancellationToken: ct))); });
app.MapGet("/api/repositories", Query("SELECT id,owner,name,clone_url AS \"cloneUrl\",default_branch AS \"defaultBranch\",is_enabled AS \"isEnabled\",last_synced_at AS \"lastSyncedAt\" FROM github.repository ORDER BY owner,name"));
app.MapGet("/api/repositories/{id:long}", async (long id, NpgsqlDataSource db, CancellationToken ct) => { await using var c = await db.OpenConnectionAsync(ct); var item = await c.QuerySingleOrDefaultAsync(new CommandDefinition("SELECT id,owner,name,clone_url AS \"cloneUrl\",default_branch AS \"defaultBranch\",is_enabled AS \"isEnabled\",created_at AS \"createdAt\",updated_at AS \"updatedAt\",last_synced_at AS \"lastSyncedAt\" FROM github.repository WHERE id=@id", new { id }, cancellationToken: ct)); return item is null ? Results.NotFound() : Results.Ok(item); });
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

public partial class Program;
