using System.Diagnostics;
using Dapper;
using Factory.Api;
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
    COALESCE(CASE WHEN t.current_agent IN ('Codex-Luna','Codex-Sol') THEN 'Codex' ELSE t.current_agent END,
      (SELECT CASE WHEN ar.agent IN ('Codex-Luna','Codex-Sol') THEN 'Codex' ELSE ar.agent END FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.purpose='Implement' ORDER BY ar.started_at DESC LIMIT 1),
      t.preferred_agent,'Codex')
    """;

const string TaskListSql = $"""
    SELECT t.id,t.title,gr.owner || '/' || gr.name AS repository,i.issue_number AS "issueNumber",t.status,t.priority,
      {TaskAgentExpr} AS agent,t.created_at AS "createdAt",t.started_at AS "startedAt",
      t.completed_at AS "completedAt",t.branch_name AS "branchName",t.worktree_path AS "worktreePath",t.failure_reason AS "failureReason",
      t.review_minutes AS "reviewMinutes",t.require_human_merge AS "requireHumanMerge",
      CASE WHEN t.failure_reason IS NOT NULL THEN t.failure_reason
           WHEN t.status IN ('Completed','ReadyForPublish','Published') THEN 'Passed'
           WHEN t.status='Rejected' THEN 'Pull request closed without merge'
           WHEN t.status='Cancelled' THEN 'Cancelled' END AS result,
      EXTRACT(EPOCH FROM (COALESCE(t.completed_at,now())-COALESCE(t.started_at,t.created_at))) AS "durationSeconds",
      CASE WHEN t.current_agent IS NULL THEN (SELECT ar.model FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.purpose='Implement' ORDER BY ar.started_at DESC LIMIT 1) END AS "agentModel",
      CASE WHEN t.current_agent IS NULL THEN (SELECT ar.reasoning_effort FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.purpose='Implement' ORDER BY ar.started_at DESC LIMIT 1) END AS "agentReasoningEffort",
      COALESCE(t.current_agent_reason,(SELECT ar.selection_reason FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.purpose='Implement' ORDER BY ar.started_at DESC LIMIT 1),t.preferred_agent_reason) AS "agentSelectionReason",
      t.agent_routing_error AS "agentRoutingError", t.task_class AS "taskClass"
    FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id LEFT JOIN github.issue i ON i.id=t.github_issue_id
    """;

static TaskResponse AddConfiguredAgentMetadata(TaskResponse task, IEnumerable<IAgentRunner> agents)
{
    var profile = agents.FirstOrDefault(agent => string.Equals(agent.Name, task.Agent, StringComparison.OrdinalIgnoreCase));
    return profile is null || profile.Name == "Codex" ? task : task with
    {
        AgentModel = task.AgentModel ?? profile.Model,
        AgentReasoningEffort = task.AgentReasoningEffort ?? profile.ReasoningEffort
    };
}

// "Busy" (ActiveTask) is read from current_agent, the live selected-at-invocation-start signal (SF-609) — never
// the task's preferred agent, which a fallback run can disagree with.
const string AgentStatsSql = """
    SELECT
      (SELECT t.title FROM factory.task t WHERE t.current_agent=ANY(@names) ORDER BY t.started_at DESC LIMIT 1) AS "ActiveTask",
      (SELECT t.task_class FROM factory.task t WHERE t.current_agent=ANY(@names) ORDER BY t.started_at DESC LIMIT 1) AS "TaskClass",
      (SELECT count(*) FROM factory.agent_run WHERE agent=ANY(@names) AND started_at >= CURRENT_DATE) AS "RunsToday",
      (SELECT count(*) FROM factory.agent_run WHERE agent=ANY(@names) AND status='Succeeded') AS "SuccessfulRuns",
      (SELECT started_at FROM factory.agent_run WHERE agent=ANY(@names) AND quota_detected=true ORDER BY started_at DESC LIMIT 1) AS "QuotaDetectedAt"
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
builder.Services.AddSingleton<NudgeStore>();
builder.Services.AddScoped<IOperatorStateResponder, OperatorChat>();
builder.Services.AddSingleton<IAssistantConversation, AssistantConversation>();
builder.Services.AddScoped<OperatorAskRouter>();
builder.Services.AddHttpClient();
builder.Services.Configure<AgentUsageOptions>(builder.Configuration.GetSection("AgentUsage"));
builder.Services.AddSingleton<IAgentUsageSnapshotStore, AgentUsageSnapshotStore>();
var configuredUsageProviders = (builder.Configuration.GetSection("Agents").Get<AgentProfilesOptions>()?.Profiles
    ?? [.. AgentProfilesOptions.DefaultProfiles]).Select(profile => profile.EffectiveProvider).ToHashSet(StringComparer.OrdinalIgnoreCase);
if (configuredUsageProviders.Contains("Codex")) builder.Services.AddSingleton<IAgentUsageProvider, CodexUsageProvider>();
if (configuredUsageProviders.Contains("Claude")) builder.Services.AddSingleton<IAgentUsageProvider, ClaudeUsageProvider>();
if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<NudgeWorker>();
    builder.Services.AddHostedService<AgentUsageWorker>();
}

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
// Actually exercises the dependency this process cannot serve any request without (SF-615) — a process that is
// merely "up" but cannot reach PostgreSQL is not healthy, and a startup script polling this needs to be able to
// tell the difference rather than getting a 200 back from a process that will 500 on its very first real request.
app.MapGet("/health", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await c.ExecuteScalarAsync(new CommandDefinition("SELECT 1", cancellationToken: ct));
        return Results.Ok(new { status = "healthy", database = "reachable" });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new { status = "unhealthy", database = "unreachable", error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/dashboard", async (NpgsqlDataSource db, IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, IEnumerable<IAgentRunner> agents, ITaskStore tasks, IAgentUsageSnapshotStore usageSnapshots, IOptions<AgentUsageOptions> usageOptions, IOptions<FactoryOptions> options, IOptions<GitHubSyncOptions> githubOptions, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var metrics = await c.QuerySingleAsync<DashboardMetricsRow>(new CommandDefinition("""
        SELECT
          count(*) FILTER (WHERE status IN ('Claimed','Preparing','Implementing','Validating','Reviewing','Stopping')) AS "ActiveTasks",
          count(*) FILTER (WHERE status='Pending') AS "PendingTasks",
          count(*) FILTER (WHERE status='Completed' AND completed_at >= CURRENT_DATE) AS "CompletedToday",
          count(*) FILTER (WHERE status='NeedsHuman') AS "NeedsOperator",
          count(*) FILTER (WHERE status IN ('ReadyForPublish','Published')) AS "ReviewBacklog",
          COALESCE(round(100.0 * count(*) FILTER (WHERE status='Completed') / NULLIF(count(*) FILTER (WHERE status IN ('Completed','Failed','Rejected')),0),1),0) AS "SuccessRate"
        FROM factory.task
        """, cancellationToken: ct));
    var active = (await c.QueryAsync<TaskRow>(new CommandDefinition(TaskListSql + " WHERE t.status IN ('Claimed','Preparing','Implementing','Validating','Reviewing','Stopping') ORDER BY t.started_at DESC LIMIT 8", cancellationToken: ct))).Select(r => AddConfiguredAgentMetadata(r.ToResponse(), agents));
    var activity = await c.QueryAsync(new CommandDefinition("SELECT s.step_type AS type,s.status,s.completed_at AS \"occurredAt\",t.title FROM factory.step s JOIN factory.run r ON r.id=s.run_id JOIN factory.task t ON t.id=r.task_id WHERE s.completed_at IS NOT NULL ORDER BY s.completed_at DESC LIMIT 12", cancellationToken: ct));
    var throughput = await c.QueryAsync(new CommandDefinition("SELECT d::date AS day,count(t.id) AS completed FROM generate_series(CURRENT_DATE-6,CURRENT_DATE,'1 day') d LEFT JOIN factory.task t ON t.completed_at::date=d::date GROUP BY d ORDER BY d", cancellationToken: ct));
    var mergeAlerts = await c.QueryAsync(new CommandDefinition("""
        SELECT m.task_id AS "taskId",t.title,m.status,m.error,m.merge_state_status AS "mergeStateStatus",m.synced_at AS "syncedAt"
        FROM factory.task_merge_status m JOIN factory.task t ON t.id=m.task_id
        WHERE t.status IN ('Published','NeedsHuman') AND m.status IN ('Conflict','Requirements','Unavailable')
        ORDER BY m.synced_at DESC LIMIT 20
        """, cancellationToken: ct));
    var mergeAttention = await c.QueryAsync(new CommandDefinition("""
        SELECT t.id AS "taskId",t.title,t.status AS "taskStatus",t.failure_reason AS "failureReason",t.validated_head_commit AS "validatedHeadCommit",
          p.pull_request_number AS "pullRequestNumber",p.pull_request_url AS "pullRequestUrl",
          ci.overall_status AS "ciStatus",ci.head_commit AS "ciHead",m.status AS "mergeStatus",
          m.merge_state_status AS "mergeStateStatus",m.mergeable,m.head_sha AS "mergeHead",
          (SELECT mr.status FROM factory.manual_merge_request mr WHERE mr.task_id=t.id ORDER BY mr.requested_at DESC LIMIT 1) AS "requestStatus"
        FROM factory.task t
        JOIN LATERAL (SELECT pull_request_number,pull_request_url FROM factory.publication
          WHERE task_id=t.id AND status='PullRequestCreated' ORDER BY completed_at DESC LIMIT 1) p ON true
        LEFT JOIN factory.task_ci_status ci ON ci.task_id=t.id
        LEFT JOIN factory.task_merge_status m ON m.task_id=t.id
        WHERE (t.status='Published' AND t.require_human_merge)
           OR (t.status='NeedsHuman' AND (t.failure_reason LIKE 'Automatic merge%' OR t.failure_reason LIKE 'On-demand merge failed:%'))
        ORDER BY t.created_at DESC LIMIT 20
        """, cancellationToken: ct));
    var attemptAlerts = await c.QueryAsync(new CommandDefinition("""
        SELECT t.id AS "taskId",t.title,t.status,t.failure_reason AS "failureReason",t.repair_paused AS "repairPaused",ci.overall_status AS "ciStatus",
          (SELECT count(*)::int FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.counts_as_implementation_attempt
            AND ar.started_at > COALESCE((SELECT max(created_at) FROM factory.task_feedback WHERE task_id=t.id),'-infinity'::timestamptz)) AS "implementation",
          NULLIF(r.repository_configuration->>'maxImplementationAttempts','')::int AS "maxImplementation",
          (SELECT count(*)::int FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.quota_detected AND NOT ar.counts_as_implementation_attempt) AS "quotaInterruptions",
          NULLIF(r.repository_configuration->>'maxQuotaInterruptions','')::int AS "maxQuotaInterruptions",
          (SELECT count(*)::int FROM factory.task_feedback f WHERE f.task_id=t.id AND f.created_by='ci-repair') AS "ciRepairs",
          (SELECT reason FROM factory.task_event e WHERE e.task_id=t.id AND e.to_status='Pending' AND e.reason IS NOT NULL ORDER BY e.occurred_at DESC LIMIT 1) AS "latestRetryReason"
        FROM factory.task t
        LEFT JOIN factory.task_ci_status ci ON ci.task_id=t.id
        LEFT JOIN LATERAL (SELECT repository_configuration FROM factory.run WHERE task_id=t.id AND repository_configuration IS NOT NULL ORDER BY started_at DESC LIMIT 1) r ON true
        WHERE t.status IN ('Pending','Claimed','Preparing','Implementing','Validating','Reviewing','WaitingForQuota','Published','NeedsHuman')
          AND (t.repair_paused
            OR (ci.overall_status='Failure' AND EXISTS (SELECT 1 FROM factory.task_feedback f WHERE f.task_id=t.id AND f.created_by='ci-repair'))
            OR (t.status IN ('Pending','Claimed','Preparing','Implementing','Validating','Reviewing','WaitingForQuota') AND EXISTS (SELECT 1 FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.counts_as_implementation_attempt
              AND ar.started_at > COALESCE((SELECT max(created_at) FROM factory.task_feedback WHERE task_id=t.id),'-infinity'::timestamptz)))
            OR (SELECT count(*) FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.quota_detected AND NOT ar.counts_as_implementation_attempt) >= 2)
        ORDER BY t.created_at DESC LIMIT 20
        """, cancellationToken: ct));
    var agentStatus = await ComputeAgentStatusAsync(c, availabilityCheckers, tasks, usageSnapshots, usageOptions.Value, ct);

    // A cap on outstanding review work (SF-612) — ReadyForPublish + Published tasks — so unattended
    // implementation can never outrun the operator's own review capacity. Zero or negative disables it.
    var reviewBacklogLimit = options.Value.MaxOutstandingReviewWork;
    var reviewBacklogAtLimit = reviewBacklogLimit > 0 && metrics.ReviewBacklog >= reviewBacklogLimit;
    var reviewBacklog = new { count = metrics.ReviewBacklog, limit = reviewBacklogLimit, atLimit = reviewBacklogAtLimit };

    // "Why is nothing running right now" — answered from the same evidence already gathered above, so the
    // operator never has to cross-reference the pending count against the agent table by hand.
    string? idleReason = metrics.ActiveTasks > 0 ? null
        : metrics.PendingTasks == 0 ? "No pending tasks queued."
        : reviewBacklogAtLimit ? $"Review backlog limit reached ({metrics.ReviewBacklog}/{reviewBacklogLimit}); merge or resolve outstanding review work to continue."
        : agentStatus.Count > 0 && agentStatus.All(a => a.State is "QuotaBlocked" or "Unavailable" or "Unknown")
            ? "No configured agent is currently available to claim work."
        : "Waiting to claim the next pending task.";

    return Results.Ok(new { metrics, active, activity, throughput, agentStatus, idleReason, reviewBacklog, mergeAlerts, mergeAttention,
        attemptAlerts = attemptAlerts.Select(row => new { row.taskId, row.title, row.status, row.failureReason, row.repairPaused, row.ciStatus,
            row.implementation, row.maxImplementation, row.quotaInterruptions, row.maxQuotaInterruptions,
            row.ciRepairs, maxCiRepairs = githubOptions.Value.MaxCiRepairAttempts,
            row.latestRetryReason }) });
});

app.MapGet("/api/execution/current", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var row = await c.QuerySingleOrDefaultAsync<CurrentExecutionRow>(new CommandDefinition(CurrentExecutionQuery.Sql, cancellationToken: ct));
    return Results.Ok(CurrentExecutionProjection.Create(row, dashboardUrl, DateTimeOffset.UtcNow));
});

app.MapPost("/api/operator/ask", async (OperatorQuestion question, OperatorAskRouter chat, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(question.Text) || question.Text.Length > 1000)
        return Results.BadRequest(new { error = "Enter a question of at most 1000 characters." });
    var history = question.History ?? [];
    if (history.Count > 20 || history.Sum(message => message.Content?.Length ?? 0) > 12_000 ||
        history.Any(message => message.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(message.Content)))
        return Results.BadRequest(new { error = "Conversation history must contain at most 20 valid user/assistant messages and 12,000 characters." });
    try
    {
        return Results.Ok(await chat.AnswerAsync(question.Text.Trim(), history, ct));
    }
    catch (AssistantCapacityException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (AssistantUnavailableException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/attention", async (NpgsqlDataSource db, IOptions<FactoryOptions> options,
    IOptions<GitHubSyncOptions> githubOptions, IEnumerable<IAgentAvailabilityChecker> availabilityCheckers,
    ITaskStore tasks, IAgentUsageSnapshotStore usageSnapshots, IOptions<AgentUsageOptions> usageOptions, CancellationToken ct) =>
{
    var now = DateTimeOffset.UtcNow;
    await using var c = await db.OpenConnectionAsync(ct);
    var taskRows = await c.QueryAsync<AttentionTaskRow>(new CommandDefinition(AttentionQuery.Tasks, cancellationToken: ct));
    var sources = (await c.QueryAsync<AttentionSourceRow>(new CommandDefinition(AttentionQuery.Sources, cancellationToken: ct))).ToList();
    var reviewCount = await c.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT count(*)::int FROM factory.task WHERE status IN ('ReadyForPublish','Published')", cancellationToken: ct));
    var reviewLimit = options.Value.MaxOutstandingReviewWork;
    if (reviewLimit > 0 && reviewCount >= reviewLimit)
        sources.Add(new AttentionSourceRow { Id = "global", Kind = "ReviewBacklog", Title = "Review backlog limit reached",
            Reason = $"{reviewCount}/{reviewLimit} tasks await publication or merge.", FirstObservedAt = now, LastObservedAt = now });
    var agents = await ComputeAgentStatusAsync(c, availabilityCheckers, tasks, usageSnapshots, usageOptions.Value, ct);
    if (agents.Count > 0 && agents.All(agent => agent.State is "QuotaBlocked" or "Unavailable" or "Unknown" or "Paused"))
        sources.Add(new AttentionSourceRow { Id = "global", Kind = "AllAgentsUnavailable", Title = "No agent can claim work",
            Reason = "Every configured agent is quota blocked, paused, or unavailable.", FirstObservedAt = now, LastObservedAt = now });
    var staleAfter = Math.Max(options.Value.PollingIntervalSeconds, options.Value.LeaseHeartbeatSeconds) * 3;
    var latestWorker = await c.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
        "SELECT max(last_seen_at) FROM factory.worker", cancellationToken: ct));
    if (AttentionProjection.StaleWorker(latestWorker, now, TimeSpan.FromSeconds(staleAfter)) is { } workerAlert)
        sources.Add(workerAlert);
    var items = AttentionProjection.Sort(AttentionProjection.ForTasks(taskRows, now, githubOptions.Value.MaxCiRepairAttempts)
        .Concat(AttentionProjection.ForSources(sources)));
    var next = reviewLimit > 0 && reviewCount >= reviewLimit ? null : await c.QuerySingleOrDefaultAsync(new CommandDefinition("""
        SELECT t.id,t.title,gr.owner || '/' || gr.name AS repository
        FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id
        WHERE t.status='Pending' AND NOT t.repair_paused AND NOT EXISTS (
          SELECT 1 FROM factory.task_dependency td JOIN factory.task dep ON dep.id=td.depends_on_task_id
          WHERE td.task_id=t.id AND dep.status<>'Completed')
        ORDER BY t.priority DESC,t.created_at LIMIT 1
        """, cancellationToken: ct));
    var pendingCount = await c.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT count(*)::int FROM factory.task WHERE status='Pending'", cancellationToken: ct));
    return Results.Ok(new { items, nextTask = next, pendingCount, workerHealthy = latestWorker is not null && now - latestWorker <= TimeSpan.FromSeconds(staleAfter) });
});

app.MapGet("/api/nudges", async (NudgeStore nudges, CancellationToken ct) =>
{
    var items = await nudges.GetRecentAsync(100, ct);
    return Results.Ok(new { items, unreadCount = await nudges.GetUnreadCountAsync(ct) });
});
app.MapPost("/api/nudges/{id:guid}/read", async (Guid id, NudgeStore nudges, CancellationToken ct) =>
    await nudges.MarkReadAsync(id, ct) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/nudges/{id:guid}/fix-conflict", async (Guid id, NudgeStore nudges, ITaskStore tasks, CancellationToken ct) =>
{
    var nudge = await nudges.GetAsync(id, ct);
    if (nudge is null) return Results.NotFound();
    if (!string.Equals(nudge.Kind, "MergeConflict", StringComparison.Ordinal) || nudge.TaskId is null || nudge.ResolvedAt is not null)
        return Results.Conflict(new { error = "This nudge is not an active merge-conflict action." });
    if (!await tasks.TriggerMergeConflictRepairAsync(nudge.TaskId.Value, ct))
        return Results.Conflict(new { error = "The pull request is no longer eligible for merge-conflict repair." });
    await nudges.MarkReadAsync(id, ct);
    return Results.Accepted($"/api/tasks/{nudge.TaskId.Value}", new { message = "Merge-conflict repair queued." });
});
app.MapPost("/api/tasks/{id:guid}/fix-conflict", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
    await tasks.TriggerMergeConflictRepairAsync(id, ct)
        ? Results.Accepted($"/api/tasks/{id}", new { message = "Merge-conflict repair queued." })
        : Results.Conflict(new { error = "The pull request is no longer eligible for merge-conflict repair." }));

app.MapGet("/api/agents/status", async (NpgsqlDataSource db, IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, ITaskStore tasks, IAgentUsageSnapshotStore usageSnapshots, IOptions<AgentUsageOptions> usageOptions, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    return Results.Ok(await ComputeAgentStatusAsync(c, availabilityCheckers, tasks, usageSnapshots, usageOptions.Value, ct));
});

app.MapGet("/api/github/status", async (IGitHubAvailabilityChecker checker, CancellationToken ct) =>
    Results.Ok(await GitHubStatusResolver.ResolveAsync(checker, ct)));

// Durable pause/resume (SF-610). Pausing stops new dispatch only — a task already claimed and executing always
// finishes, and publication of already-validated work is untouched, since it consumes no agent's subscription.
app.MapGet("/api/control/pause", async (ITaskStore tasks, CancellationToken ct) =>
    Results.Ok(await tasks.GetAllDispatchPausesAsync(ct)));

app.MapGet("/api/control/history", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    return Results.Ok(await c.QueryAsync(new CommandDefinition("""
        SELECT id,scope,paused,reason,actor,occurred_at AS "occurredAt"
        FROM factory.dispatch_pause_event ORDER BY occurred_at DESC,id DESC LIMIT 50
        """, cancellationToken: ct)));
});

app.MapPost("/api/control/pause", async (PauseRequest? body, HttpRequest request, ITaskStore tasks, CancellationToken ct) =>
{
    var header = request.Headers["X-Expected-Paused"].ToString();
    if (header.Length > 0 && !bool.TryParse(header, out _)) return Results.BadRequest(new { error = "Invalid expected pause state." });
    bool? expected = header.Length == 0 ? null : bool.Parse(header);
    return await tasks.SetDispatchPauseAsync(DispatchPauseScope.Global, true, body?.Reason, "operator", ct, expected)
        ? Results.NoContent() : Results.Conflict(new { error = "Dispatch state changed. Refresh before pausing." });
});

app.MapPost("/api/control/resume", async (HttpRequest request, ITaskStore tasks, CancellationToken ct) =>
{
    var header = request.Headers["X-Expected-Paused"].ToString();
    if (header.Length > 0 && !bool.TryParse(header, out _)) return Results.BadRequest(new { error = "Invalid expected pause state." });
    bool? expected = header.Length == 0 ? null : bool.Parse(header);
    return await tasks.SetDispatchPauseAsync(DispatchPauseScope.Global, false, null, "operator", ct, expected)
        ? Results.NoContent() : Results.Conflict(new { error = "Dispatch state changed. Refresh before resuming." });
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

// Operator override for a stale quota-detected status: a detected reset time is at best a bounded
// estimate (see QuotaResetPattern in README.md), and one that outlives the provider's real, shorter reset used
// to be recoverable only by editing factory.agent_availability directly. This gives that same recovery a proper,
// audited operator control, matching how pause/resume already replaced ad hoc database edits.
app.MapPost("/api/agents/{agent}/clear-quota", async (string agent, ITaskStore tasks, CancellationToken ct) =>
{
    var cleared = await tasks.ClearAgentQuotaAsync(agent, ct);
    return cleared ? Results.NoContent() : Results.NotFound(new { error = $"No quota status recorded for agent '{agent}'." });
});

app.MapGet("/api/tasks", async (string? status, string? repository, string? agent, string? q, string? sort, string? direction, int? page, int? pageSize, NpgsqlDataSource db, IEnumerable<IAgentRunner> agents, CancellationToken ct) =>
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
    var items = (await c.QueryAsync<TaskRow>(new CommandDefinition(TaskListSql + where + $" ORDER BY {query.SortExpression} {query.Direction}, t.id LIMIT @Size OFFSET @Offset", new { status, repository, agent, q, query.Size, Offset = offset }, cancellationToken: ct))).Select(r => AddConfiguredAgentMetadata(r.ToResponse(), agents));
    return Results.Ok(new { items, total, page = normalizedPage, pageSize = query.Size });
});

app.MapGet("/api/tasks/{id:guid}", async (Guid id, NpgsqlDataSource db, ITaskStore tasks, IEnumerable<IAgentRunner> agents, IOptions<GitHubSyncOptions> githubOptions, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.get_task");
    activity?.SetTag("factory.task_id", id);
    await using var c = await db.OpenConnectionAsync(ct);
    var taskRow = await c.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(TaskListSql + " WHERE t.id=@id", new { id }, cancellationToken: ct));
    if (taskRow is null) return Results.NotFound();
    var task = AddConfiguredAgentMetadata(taskRow.ToResponse(), agents);
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
        SELECT id,run_id AS "RunId",agent,purpose,model,reasoning_effort AS "ReasoningEffort",selection_reason AS "SelectionReason",task_class AS "TaskClass",
          started_at AS "StartedAt",completed_at AS "CompletedAt",duration_seconds AS "DurationSeconds",
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
    var feedback = await tasks.GetFeedbackAsync(id, ct);
    var ciStatus = await tasks.GetCiStatusAsync(id, ct);
    var mergeStatus = await tasks.GetMergeStatusAsync(id, ct);
    var mergeRequests = await c.QueryAsync(new CommandDefinition("""
        SELECT id,requested_by AS "requestedBy",requested_at AS "requestedAt",completed_at AS "completedAt",
          status,head_sha AS "headSha",error
        FROM factory.manual_merge_request WHERE task_id=@id ORDER BY requested_at DESC
        """, new { id }, cancellationToken: ct));
    var taskEvents = await c.QueryAsync(new CommandDefinition("""
        SELECT id,from_status AS "fromStatus",to_status AS "toStatus",reason,actor,occurred_at AS "occurredAt"
        FROM factory.task_event WHERE task_id=@id ORDER BY occurred_at DESC,id DESC LIMIT 100
        """, new { id }, cancellationToken: ct));
    var attemptRow = await c.QuerySingleAsync(new CommandDefinition("""
        SELECT t.repair_paused AS "repairPaused",t.validated_head_commit AS "validatedHeadCommit",
          (SELECT count(*)::int FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.counts_as_implementation_attempt
            AND ar.started_at > COALESCE((SELECT max(created_at) FROM factory.task_feedback WHERE task_id=t.id),'-infinity'::timestamptz)) AS "implementation",
          (SELECT count(*)::int FROM factory.agent_run ar WHERE ar.task_id=t.id AND ar.quota_detected AND NOT ar.counts_as_implementation_attempt) AS "quotaInterruptions",
          (SELECT count(*)::int FROM factory.task_feedback f WHERE f.task_id=t.id AND f.created_by='ci-repair') AS "ciRepairs",
          (SELECT NULLIF(r.repository_configuration->>'maxImplementationAttempts','')::int FROM factory.run r WHERE r.task_id=t.id AND r.repository_configuration IS NOT NULL ORDER BY r.started_at DESC LIMIT 1) AS "maxImplementation",
          (SELECT NULLIF(r.repository_configuration->>'maxQuotaInterruptions','')::int FROM factory.run r WHERE r.task_id=t.id AND r.repository_configuration IS NOT NULL ORDER BY r.started_at DESC LIMIT 1) AS "maxQuotaInterruptions",
          (SELECT reason FROM factory.task_event e WHERE e.task_id=t.id AND e.to_status='Pending' AND e.reason IS NOT NULL ORDER BY e.occurred_at DESC LIMIT 1) AS "latestRetryReason"
        FROM factory.task t WHERE t.id=@id
        """, new { id }, cancellationToken: ct));
    var attempts = new { attemptRow.repairPaused, attemptRow.implementation, attemptRow.quotaInterruptions,
        attemptRow.ciRepairs, attemptRow.maxImplementation, attemptRow.maxQuotaInterruptions,
        maxCiRepairs = githubOptions.Value.MaxCiRepairAttempts, attemptRow.latestRetryReason };
    var reviewFindings = await tasks.GetReviewFindingsAsync(id, ct);
    var dependencyDtos = dependencies.Select(d => new { d.TaskId, d.DependsOnTaskId, d.DependsOnTitle, DependsOnStatus = d.DependsOnStatus.ToString(), d.Source });
    return Results.Ok(new { task, issue, comments, runs, steps, agentRuns, publications, dependencies = dependencyDtos, feedback, ciStatus, mergeStatus, mergeRequests, taskEvents,
        validatedHeadCommit = attemptRow.validatedHeadCommit, attempts, reviewFindings });
});

app.MapPost("/api/tasks/{id:guid}/stop-repairs", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
    await tasks.SetRepairPausedAsync(id, true, "operator", ct)
        ? Results.Accepted($"/api/tasks/{id}") : Results.Conflict(new { error = "Automatic attempts are already stopped or this task cannot be stopped." }));

app.MapPost("/api/tasks/{id:guid}/resume-repairs", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
    await tasks.SetRepairPausedAsync(id, false, "operator", ct)
        ? Results.Accepted($"/api/tasks/{id}") : Results.Conflict(new { error = "Automatic attempts are already enabled or this task cannot be resumed." }));

app.MapPost("/api/tasks/{id:guid}/merge", async (Guid id, ITaskStore tasks, IGitHubClient github, IGitHubPublisher publisher, CancellationToken ct) =>
{
    var request = await tasks.BeginManualMergeAsync(id, "operator", ct);
    if (request is null) return Results.Conflict(new { error = "Task has no eligible factory-owned PR, or a merge request is already active or succeeded." });

    async Task<IResult> Fail(string reason, int statusCode = 409, bool githubRejected = false)
    {
        await tasks.CompleteManualMergeAsync(request.Id, false, null, reason, githubRejected, ct);
        return Results.Json(new { error = reason }, statusCode: statusCode);
    }

    try
    {
        var pr = await github.GetPullRequestMergeabilityAsync(request.RepositoryOwner, request.RepositoryName, request.PullRequestNumber, ct);
        if (!pr.Succeeded) return await Fail($"GitHub PR read failed: {pr.Error}", 502);
        var checks = await github.GetPullRequestChecksAsync(request.RepositoryOwner, request.RepositoryName, request.PullRequestNumber, ct);
        if (!checks.Succeeded) return await Fail($"GitHub CI read failed: {checks.Error}", 502);
        var refusal = ManualMergeGuard.Refusal(request, pr, checks);
        if (refusal is not null) return await Fail(refusal);
        if (pr.IsDraft)
        {
            var ready = await publisher.ReadyPullRequestAsync(request.RepositoryOwner, request.RepositoryName, request.PullRequestNumber, ct);
            if (!ready.Succeeded) return await Fail($"GitHub could not mark the draft ready: {ready.Error}", 502);
            pr = await github.GetPullRequestMergeabilityAsync(request.RepositoryOwner, request.RepositoryName, request.PullRequestNumber, ct);
            refusal = ManualMergeGuard.Refusal(request, pr, checks);
            if (refusal is not null) return await Fail($"Pull request changed after it was marked ready: {refusal}");
        }
        if (pr.IsDraft) return await Fail("Pull request is still a draft after GitHub accepted the ready request.");
        var result = await publisher.MergePullRequestAtHeadAsync(request.RepositoryOwner, request.RepositoryName,
            request.PullRequestNumber, checks.HeadSha!, ct);
        if (!result.Succeeded) return await Fail($"GitHub rejected the merge: {result.Error}", 502, githubRejected: true);
        await tasks.CompleteManualMergeAsync(request.Id, true, checks.HeadSha, null, false, ct);
        return Results.Accepted($"/api/tasks/{id}", new { message = "Merge accepted. Task completion will reconcile on the next sync." });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return await Fail($"Merge request failed: {ex.Message}", 502);
    }
});

app.MapPost("/api/tasks/{id:guid}/retry", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.retry_task");
    activity?.SetTag("factory.task_id", id);
    return await tasks.RetryAsync(id, ct) ? Results.Accepted($"/api/tasks/{id}") : Results.Conflict(new { error = "Task cannot be retried from its current state." });
});

app.MapPost("/api/tasks/{id:guid}/continue", async (Guid id, ContinueRequest body, ITaskStore tasks, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Feedback)) return Results.BadRequest(new { error = "Feedback is required." });
    using var activity = FactoryTelemetry.Source.StartActivity("api.continue_task");
    activity?.SetTag("factory.task_id", id);
    return await tasks.ContinueWithFeedbackAsync(id, body.Feedback, ct)
        ? Results.Accepted($"/api/tasks/{id}")
        : Results.Conflict(new { error = "Task cannot be continued from its current state." });
});

app.MapPost("/api/tasks/{id:guid}/review-time", async (Guid id, ReviewTimeRequest body, ITaskStore tasks, CancellationToken ct) =>
{
    if (body.Minutes < 0) return Results.BadRequest(new { error = "Minutes must not be negative." });
    using var activity = FactoryTelemetry.Source.StartActivity("api.set_review_minutes");
    activity?.SetTag("factory.task_id", id);
    await tasks.SetReviewMinutesAsync(id, body.Minutes, ct);
    return Results.NoContent();
});

// Small outcome/review-effort metrics over a rolling window (SF-617), defaulting to the last 7 days —
// deliberately excludes lines changed and consumed quota as productivity signals, and exposes no "remaining
// quota" figure (see OutcomeMetrics's own doc comment for why).
app.MapGet("/api/metrics", async (int? days, ITaskStore tasks, CancellationToken ct) =>
{
    var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(days ?? 7, 1));
    return Results.Ok(await tasks.GetOutcomeMetricsAsync(since, ct));
});

// The most recently generated digest (SF-705) — finished work, open CI failures, items needing the developer,
// and meaningful quota/worker blockers, with unchanged alerts already suppressed by DigestBuilder at generation
// time. history=N (default 0) also returns that many prior digests, oldest-first-of-that-slice reversed to
// newest-first, for a short "recent digests" view.
app.MapGet("/api/digest", async (int? history, IDigestStore digests, CancellationToken ct) =>
{
    var latest = await digests.GetLatestAsync(ct);
    if (latest is null) return Results.Ok(new { latest = (DigestRun?)null, history = Array.Empty<DigestRun>() });
    var recent = history is > 0 ? await digests.GetRecentAsync(history.Value + 1, ct) : [];
    return Results.Ok(new { latest, history = recent.Where(d => d.Id != latest.Id) });
});

app.MapPost("/api/tasks/{id:guid}/cancel", async (Guid id, ITaskStore tasks, CancellationToken ct) =>
{
    using var activity = FactoryTelemetry.Source.StartActivity("api.cancel_task");
    activity?.SetTag("factory.task_id", id);
    var outcome = await tasks.CancelAsync(id, ct);
    return outcome switch
    {
        TaskCancellationOutcome.Stopping => Results.Accepted($"/api/tasks/{id}", new { status = "Stopping" }),
        TaskCancellationOutcome.Cancelled => Results.Ok(new { status = "Cancelled" }),
        _ => Results.Conflict(new { error = "Task cannot be cancelled." })
    };
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
          array_agg(DISTINCT label.name ORDER BY label.name) FILTER (WHERE label.name IS NOT NULL) AS labels,
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

// The dashboard's own path to flipping `factory:ready`, so an operator never has to leave the UI to run
// `gh issue edit`. Writes straight to GitHub via IIssueReadyLabelWriter; the imported issue's own `eligible`
// flag catches up on the next Sync poll cycle, same latency as every other GitHub-sourced dashboard field.
app.MapPatch("/api/issues/{id:long}/ready", async (long id, SetIssueReadyRequest body, IGitHubStore github, IIssueReadyLabelWriter labels, CancellationToken ct) =>
{
    var issue = await github.GetIssueAsync(id, ct);
    if (issue is null) return Results.NotFound(new { error = $"No issue with id {id}." });
    var repository = await github.GetRepositoryAsync(issue.RepositoryId, ct);
    if (repository is null) return Results.NotFound(new { error = "This issue's repository is no longer configured." });
    var result = await labels.SetReadyAsync(repository.Owner, repository.Name, issue.IssueNumber, body.IsReady, ct);
    return result.Succeeded ? Results.NoContent() : Results.Problem(result.Error, statusCode: 502);
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
        SELECT id,run_id AS "RunId",agent,purpose,model,reasoning_effort AS "ReasoningEffort",selection_reason AS "SelectionReason",task_class AS "TaskClass",
          started_at AS "StartedAt",completed_at AS "CompletedAt",duration_seconds AS "DurationSeconds",
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

// SF-711: an operator adding a repository here needs no service restart — Factory.GitHubSync.Worker already
// re-reads IGitHubStore.GetEnabledRepositoriesAsync every poll cycle, so this row is picked up on the next cycle.
app.MapPost("/api/repositories", async (AddRepositoryRequest body, IGitHubStore github, CancellationToken ct) =>
{
    var owner = body.Owner?.Trim() ?? "";
    var name = body.Name?.Trim() ?? "";
    if (!RepositoryNameValidator.IsValid(owner) || !RepositoryNameValidator.IsValid(name))
        return Results.BadRequest(new { error = "Owner and name must be non-empty and use only letters, digits, '.', '_', or '-'." });
    var cloneUrl = string.IsNullOrWhiteSpace(body.CloneUrl) ? $"https://github.com/{owner}/{name}.git" : body.CloneUrl.Trim();
    var defaultBranch = string.IsNullOrWhiteSpace(body.DefaultBranch) ? "main" : body.DefaultBranch.Trim();
    var repository = await github.AddRepositoryAsync(owner, name, cloneUrl, defaultBranch, ct);
    return Results.Ok(repository);
});

app.MapPost("/api/repositories/bootstrap", async (BootstrapRepositoryRequest body, IRepositoryBootstrapper bootstrapper, CancellationToken ct) =>
{
    var owner = body.Owner?.Trim() ?? "";
    var name = body.Name?.Trim() ?? "";
    if (!RepositoryNameValidator.IsValid(owner) || !RepositoryNameValidator.IsValid(name))
        return Results.BadRequest(new { error = "Owner and name must be non-empty and use only letters, digits, '.', '_', or '-'." });
    if (!Enum.TryParse<ApplicationShell>(body.Shell, ignoreCase: true, out var shell))
        return Results.BadRequest(new { error = "Shell must be dashboard, mobile, or both." });
    var visibility = string.IsNullOrWhiteSpace(body.Visibility) ? "private" : body.Visibility.Trim().ToLowerInvariant();
    if (visibility is not ("private" or "public" or "internal"))
        return Results.BadRequest(new { error = "Visibility must be private, public, or internal." });
    if (new[] { body.ProductName, body.FirstJourney, body.BackendChoice, body.AuthenticationProvider, body.DeployTarget }.Any(string.IsNullOrWhiteSpace))
        return Results.BadRequest(new { error = "Every product brief field is required." });

    if (body.ExistingRepositoryUrl is not null)
    {
        var expected = $"https://github.com/{owner}/{name}";
        var supplied = body.ExistingRepositoryUrl.Trim().TrimEnd('/');
        if (!string.Equals(supplied, expected, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(supplied, expected + ".git", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = $"ExistingRepositoryUrl must identify {owner}/{name} on github.com." });
    }

    var brief = new ProductBrief(body.ProductName, body.FirstJourney, body.BackendChoice, body.AuthenticationProvider, body.DeployTarget, shell);
    try
    {
        var result = await bootstrapper.BootstrapAsync(new RepositoryBootstrapRequest(owner, name, brief, visibility, body.ExistingRepositoryUrl), ct);
        return Results.Ok(result);
    }
    catch (RepositoryBootstrapException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPatch("/api/repositories/{id:long}", async (long id, SetRepositoryEnabledRequest body, IGitHubStore github, CancellationToken ct) =>
{
    var updated = await github.SetRepositoryEnabledAsync(id, body.IsEnabled, ct);
    return updated ? Results.NoContent() : Results.NotFound(new { error = $"No repository with id {id}." });
});

// SF-717: a read-only schema browser plus ad-hoc SELECT queries so staying on top of what the factory is doing
// doesn't require psql. The keyword check in SqlSelectValidator is only a first-pass rejection — the real
// enforcement is the database session itself, which refuses any write inside a READ ONLY transaction.
app.MapGet("/api/database/tables", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var rows = await c.QueryAsync<DatabaseColumnRow>(new CommandDefinition("""
        SELECT table_schema AS "TableSchema", table_name AS "TableName", column_name AS "ColumnName", data_type AS "DataType"
        FROM information_schema.columns
        WHERE table_schema IN ('factory','github')
        ORDER BY table_schema, table_name, ordinal_position
        """, cancellationToken: ct));
    var tables = rows.GroupBy(r => (r.TableSchema, r.TableName)).Select(g => new
    {
        schema = g.Key.TableSchema,
        table = g.Key.TableName,
        columns = g.Select(r => new { name = r.ColumnName, type = r.DataType })
    });
    return Results.Ok(tables);
});

app.MapPost("/api/database/query", async (DatabaseQueryRequest body, NpgsqlDataSource db, IOptions<FactoryOptions> options, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!SqlSelectValidator.IsReadOnlySelect(body.Sql, out var validationError))
        return Results.BadRequest(new { error = validationError });

    logger.LogInformation("Executing ad-hoc database query: {Sql}", body.Sql);

    var rowLimit = Math.Max(1, options.Value.DatabaseQueryRowLimit);
    var timeoutSeconds = Math.Max(1, options.Value.DatabaseQueryTimeoutSeconds);

    await using var connection = await db.OpenConnectionAsync(ct);
    await connection.ExecuteAsync(new CommandDefinition("BEGIN TRANSACTION READ ONLY", cancellationToken: ct));
    try
    {
        await connection.ExecuteAsync(new CommandDefinition($"SET LOCAL statement_timeout = {timeoutSeconds * 1000}", cancellationToken: ct));
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(body.Sql, cancellationToken: ct));
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= rowLimit) { truncated = true; break; }
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
        return Results.Ok(new { columns, rows, rowCount = rows.Count, truncated });
    }
    catch (PostgresException ex)
    {
        return Results.BadRequest(new { error = ex.MessageText });
    }
    finally
    {
        await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
    }
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
app.MapGet("/api/agents/{agent}/runs", async (string agent, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    var names = agent == "Codex" ? new[] { "Codex", "Codex-Luna", "Codex-Sol" } : new[] { agent };
    return Results.Ok(await c.QueryAsync(new CommandDefinition("""
        SELECT id,task_id AS "taskId",run_id AS "runId",agent,model,reasoning_effort AS "reasoningEffort",task_class AS "taskClass",
          started_at AS "startedAt",completed_at AS "completedAt",duration_seconds AS "durationSeconds",exit_code AS "exitCode",
          status,quota_detected AS "quotaDetected",quota_reset_at AS "quotaResetAt",attempt_number AS "attemptNumber",needs_human AS "needsHuman"
        FROM factory.agent_run WHERE agent=ANY(@names) ORDER BY started_at DESC LIMIT 100
        """, new { names }, cancellationToken: ct)));
});
app.MapGet("/api/metrics/summary", Query("SELECT count(*) AS attempted,count(*) FILTER(WHERE status='Completed') AS completed,count(*) FILTER(WHERE status='NeedsHuman') AS \"humanInterventions\" FROM factory.task"));
app.MapGet("/api/metrics/throughput", Query("SELECT completed_at::date AS day,count(*) AS completed FROM factory.task WHERE completed_at IS NOT NULL GROUP BY 1 ORDER BY 1"));
app.MapGet("/api/metrics/agents", Query("""
    SELECT CASE WHEN agent IN ('Codex-Luna','Codex-Sol') THEN 'Codex' ELSE agent END AS agent,
      count(*) AS runs,count(*) FILTER(WHERE status='Succeeded') AS successful,avg(duration_seconds) AS "averageDurationSeconds"
    FROM factory.agent_run GROUP BY 1
    """));

await app.RunAsync();

static Func<NpgsqlDataSource, CancellationToken, Task<IResult>> Query(string sql) => async (db, ct) =>
{
    await using var c = await db.OpenConnectionAsync(ct);
    return Results.Ok(await c.QueryAsync(new CommandDefinition(sql, cancellationToken: ct)));
};

// Shared by /api/dashboard and /api/agents/status so the header's compact status pill and the full Overview
// panel can never disagree about an agent's state.
static async Task<List<AgentStatus>> ComputeAgentStatusAsync(NpgsqlConnection c, IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, ITaskStore tasks, IAgentUsageSnapshotStore usageSnapshots, AgentUsageOptions usageOptions, CancellationToken ct)
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

        // Old preset-named rows prove only provider-level readiness. Keep them in Codex throughput totals;
        // model-specific evidence remains on each historical invocation, not on this provider status.
        var names = checker.Agent == "Codex" ? new[] { "Codex", "Codex-Luna", "Codex-Sol" } : new[] { checker.Agent };
        var stats = await c.QuerySingleAsync<AgentStatsRow>(new CommandDefinition(AgentStatsSql, new { names }, cancellationToken: ct));
        var isAtQuota = await tasks.IsAgentAtQuotaAsync(checker.Provider, ct);
        var quotaStatus = await tasks.GetAgentQuotaStatusAsync(checker.Provider, ct);
        var pause = await tasks.GetDispatchPauseAsync(checker.Provider, ct);
        var state = AgentOperationalStateResolver.Resolve(succeeded, errored, pause.Paused, isAtQuota, stats.ActiveTask is not null, stats.SuccessfulRuns > 0);
        var (quotaResetAt, quotaWindow, quotaResetKind) = isAtQuota && quotaStatus is not null
            ? (quotaStatus.ResetAt, quotaStatus.Window.ToString(), quotaStatus.ResetKind.ToString())
            : (null, null, null);
        agentStatus.Add(new AgentStatus(checker.Agent, state.ToString(), version, error,
            stats.ActiveTask, stats.TaskClass, stats.RunsToday, stats.SuccessfulRuns, stats.QuotaDetectedAt, quotaResetAt, quotaWindow, quotaResetKind,
            pause.Paused ? pause.Reason : null, usageSnapshots.GetOrUnknown(checker.Provider, DateTimeOffset.UtcNow),
            usageOptions.WarningThresholdPercent, usageOptions.CriticalThresholdPercent));
    }
    return agentStatus;
}

public partial class Program;
