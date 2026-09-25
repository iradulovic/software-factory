using System.Text.RegularExpressions;
using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.Api;

public sealed record OperatorQuestion(string Text);
public sealed record OperatorEvidence(string Label, string Detail, string Href);
public sealed record OperatorAction(string Label, string Path, string OutcomePath, bool? ExpectedPaused = null);
public sealed record OperatorReply(string Observed, string? Explanation, string? Suggestion,
    IReadOnlyList<OperatorEvidence> Evidence, OperatorAction? Action, DateTimeOffset AsOf);

public static partial class OperatorQuestionParser
{
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex TaskIdPattern();

    public static (string Intent, Guid? TaskId, bool Ambiguous) Parse(string question)
    {
        var ids = TaskIdPattern().Matches(question).Select(m => Guid.Parse(m.Value)).Distinct().ToArray();
        var q = question.ToLowerInvariant();
        var intent = q.Contains("stop") && (q.Contains("retr") || q.Contains("repair")) ? "stop-repairs"
            : q.Contains("cancel") ? "cancel"
            : q.Contains("merge") ? "merge"
            : q.Contains("resume") && (q.Contains("dispatch") || q.Contains("factory")) ? "resume"
            : q.Contains("pause") && (q.Contains("dispatch") || q.Contains("factory")) ? "pause"
            : q.Contains("retry") || q.Contains("retried") ? "retry"
            : q.Contains("running") || q.Contains("working on") ? "running"
            : q.Contains("idle") || q.Contains("nothing happening") ? "idle"
            : q.Contains("changed") || q.Contains("today") ? "changed"
            : q.Contains("need") && (q.Contains("me") || q.Contains("attention")) ? "attention"
            : q.Contains("do next") ? "attention"
            : q.Contains("next") ? "next"
            : "help";
        return (intent, ids.Length == 1 ? ids[0] : null, ids.Length > 1);
    }
}

public static class OperatorActionEligibility
{
    // The existing API remains authoritative. This only decides whether a proposal is useful to show.
    public static bool CanOffer(string intent, string status, bool repairPaused) => intent switch
    {
        "cancel" => status is "Pending" or "WaitingForQuota" or "Claimed" or "Preparing" or "Planning" or "Implementing" or "Validating" or "Reviewing" or "ReadyForPublish" or "NeedsHuman" or "Failed",
        "stop-repairs" => !repairPaused && status is not ("Completed" or "Cancelled" or "Rejected"),
        _ => false
    };
}

public sealed class OperatorChat(NpgsqlDataSource dataSource, ITaskStore tasks, IOptions<GitHubSyncOptions> githubOptions,
    IOptions<FactoryOptions> factoryOptions)
{
    public async Task<OperatorReply> AnswerAsync(string question, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (intent, taskId, ambiguous) = OperatorQuestionParser.Parse(question);
        if (ambiguous) return Reply("The request names more than one task.", "I cannot choose which task to change.",
            "Ask again with exactly one task ID.", now);
        await using var db = await dataSource.OpenConnectionAsync(ct);
        if (intent is "pause" or "resume")
        {
            var pause = await tasks.GetDispatchPauseAsync(DispatchPauseScope.Global, ct);
            var wanted = intent == "pause";
            return pause.Paused == wanted
                ? Reply(wanted ? "Dispatch is already paused." : "Dispatch is already running.", null,
                    "No action is needed.", now, [new("Dispatch control", pause.Reason ?? "Current persisted control state", "/")])
                : Reply(wanted ? "Dispatch is currently running." : "Dispatch is currently paused.", null,
                    "Confirm the proposed control action below.", now,
                    [new("Dispatch control", pause.Reason ?? "Current persisted control state", "/")],
                    new(wanted ? "Pause new dispatch" : "Resume new dispatch", wanted ? "/api/control/pause" : "/api/control/resume", "/api/control/pause", pause.Paused));
        }
        if (intent is "cancel" or "stop-repairs" or "merge" or "retry")
        {
            if (taskId is null) return Reply("No single task was identified.", null,
                "Open a task and ask again with its full task ID.", now);
            var task = await db.QuerySingleOrDefaultAsync<OperatorTaskRow>(new CommandDefinition("""
                SELECT t.id,t.title,t.status,t.repair_paused AS "RepairPaused",
                  gr.owner AS "RepositoryOwner",gr.name AS "RepositoryName",i.issue_number AS "IssueNumber",
                  p.pull_request_url AS "PullRequestUrl"
                FROM factory.task t
                JOIN github.repository gr ON gr.id=t.repository_id
                LEFT JOIN github.issue i ON i.id=t.github_issue_id
                LEFT JOIN LATERAL (SELECT pull_request_url FROM factory.publication
                    WHERE task_id=t.id AND status='PullRequestCreated' ORDER BY completed_at DESC LIMIT 1) p ON true
                WHERE t.id=@taskId
                """, new { taskId }, cancellationToken: ct));
            if (task is null) return Reply("That task no longer exists.", null, "Refresh the task list.", now);
            var evidence = new List<OperatorEvidence> { new("Task state", $"{task.Title} · {task.Status}", $"/tasks/{task.Id}") };
            if (task.IssueNumber is { } issue)
                evidence.Add(new("GitHub issue", $"{task.RepositoryOwner}/{task.RepositoryName}#{issue}",
                    $"https://github.com/{Uri.EscapeDataString(task.RepositoryOwner)}/{Uri.EscapeDataString(task.RepositoryName)}/issues/{issue}"));
            if (task.PullRequestUrl is { } pullRequestUrl)
                evidence.Add(new("Pull request", pullRequestUrl, pullRequestUrl));
            if (intent == "retry")
            {
                var events = (await db.QueryAsync<OperatorEventRow>(new CommandDefinition("""
                    SELECT from_status AS "FromStatus",to_status AS "ToStatus",reason AS "Reason",occurred_at AS "OccurredAt"
                    FROM factory.task_event WHERE task_id=@taskId ORDER BY occurred_at DESC,id DESC LIMIT 5
                    """, new { taskId }, cancellationToken: ct))).ToArray();
                var retries = events.Where(e => e.ToStatus == "Pending" && e.FromStatus != "Pending").ToArray();
                var latest = retries.FirstOrDefault();
                return Reply(latest is null ? "No recent retry transition was recorded." :
                    $"The latest retry was recorded at {latest.OccurredAt:u}: {latest.Reason ?? "No reason recorded."}",
                    "This is the persisted transition reason; inspect the linked task and runs for the full failure history.",
                    null, now, evidence);
            }
            var eligible = intent switch
            {
                "cancel" or "stop-repairs" => OperatorActionEligibility.CanOffer(intent, task.Status, task.RepairPaused),
                "merge" => await IsMergeReadyAsync(db, task.Id, now, githubOptions.Value.MaxCiRepairAttempts, ct),
                _ => false
            };
            if (!eligible) return Reply($"{task.Title} is {task.Status}; the requested action is not currently eligible.",
                "The live API will recheck eligibility when an action is submitted.", "Review the linked task state.", now, evidence);
            var action = intent switch
            {
                "cancel" => new OperatorAction("Cancel task", $"/api/tasks/{task.Id}/cancel", $"/api/tasks/{task.Id}"),
                "stop-repairs" => new OperatorAction("Stop future repairs", $"/api/tasks/{task.Id}/stop-repairs", $"/api/tasks/{task.Id}"),
                _ => new OperatorAction("Merge pull request", $"/api/tasks/{task.Id}/merge", $"/api/tasks/{task.Id}")
            };
            return Reply($"{task.Title} is {task.Status}.", "The action is eligible in the latest recorded state; the existing API checks it again on submission.",
                "Confirm the proposed action below.", now, evidence, action);
        }
        if (intent == "running")
        {
            var row = await db.QuerySingleOrDefaultAsync<CurrentExecutionRow>(new CommandDefinition(CurrentExecutionQuery.Sql, cancellationToken: ct));
            var current = CurrentExecutionProjection.Create(row, "", now);
            return row is null ? Reply("No task is currently executing.", null, "Ask why the factory is idle for possible causes.", now,
                [new("Current execution", "No active task", "/")])
                : Reply($"{row.TaskTitle} is {current.Status.ToLowerInvariant()} ({row.TaskStatus}).",
                    current.StepType is null ? "No step is currently running." : $"Current step: {current.StepType}.", null, now,
                    row.IssueNumber is null
                        ? [new("Task", row.TaskTitle, $"/tasks/{row.TaskId}"),
                           new("Run", row.RunId?.ToString() ?? "Run has not started", row.RunId is null ? $"/tasks/{row.TaskId}" : $"/runs/{row.RunId}")]
                        : [new("Task", row.TaskTitle, $"/tasks/{row.TaskId}"),
                           new("Run", row.RunId?.ToString() ?? "Run has not started", row.RunId is null ? $"/tasks/{row.TaskId}" : $"/runs/{row.RunId}"),
                           new("GitHub issue", $"#{row.IssueNumber}", current.IssueUrl!)]);
        }
        if (intent == "idle")
        {
            var active = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM factory.task WHERE status IN ('Claimed','Preparing','Planning','Implementing','Validating','Reviewing','Stopping')", cancellationToken: ct));
            var pending = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT count(*)::int FROM factory.task WHERE status='Pending'", cancellationToken: ct));
            var paused = await tasks.GetDispatchPauseAsync(DispatchPauseScope.Global, ct);
            var next = await tasks.GetNextEligibleTaskAsync(ct);
            var lastWorker = await db.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
                "SELECT max(last_seen_at) FROM factory.worker", cancellationToken: ct));
            var workerStaleAfter = TimeSpan.FromSeconds(Math.Max(factoryOptions.Value.PollingIntervalSeconds,
                factoryOptions.Value.LeaseHeartbeatSeconds) * 3);
            var reason = active > 0 ? "A task is executing."
                : paused.Paused ? $"Dispatch is paused{(paused.Reason is null ? "." : $": {paused.Reason}")}"
                : pending == 0 ? "No tasks are pending."
                : lastWorker is null || now - lastWorker > workerStaleAfter ? "The orchestrator has no recent worker heartbeat."
                : next is null ? "Pending tasks exist, but none is currently eligible under dependencies and dispatch policy."
                : "An eligible task is queued. Check provider status if the worker does not claim it.";
            return Reply($"{active} active task(s); {pending} pending task(s).", reason,
                next is null ? "Inspect attention and task dependencies." : $"Next eligible: {next.Title}.", now,
                next is null ? [new("Task queue", "Pending and active task records", "/tasks")]
                    : [new("Next eligible task", next.Title, $"/tasks/{next.TaskId}")]);
        }
        if (intent == "changed")
        {
            var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var rows = (await db.QueryAsync<OperatorChangeRow>(new CommandDefinition("""
                SELECT e.task_id AS "TaskId",t.title AS "Title",e.to_status AS "Status",e.reason AS "Reason",e.occurred_at AS "OccurredAt"
                FROM factory.task_event e JOIN factory.task t ON t.id=e.task_id
                WHERE e.occurred_at >= @start AND e.occurred_at < @now
                ORDER BY e.occurred_at DESC,e.id DESC LIMIT 10
                """, new { start, now }, cancellationToken: ct))).ToArray();
            var count = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT count(*)::int FROM factory.task_event WHERE occurred_at >= @start AND occurred_at < @now
                """, new { start, now }, cancellationToken: ct));
            return Reply($"{count} task transition(s) were recorded since {start:u} (UTC).", null,
                count == 0 ? "No task state changed in this window." : "Open a task to inspect its run history.", now,
                rows.Select(r => new OperatorEvidence("Task transition", $"{r.Title}: {r.Status} at {r.OccurredAt:u}", $"/tasks/{r.TaskId}")).ToArray());
        }
        if (intent == "attention")
        {
            var taskRows = await db.QueryAsync<AttentionTaskRow>(new CommandDefinition(AttentionQuery.Tasks, cancellationToken: ct));
            var sourceRows = await db.QueryAsync<AttentionSourceRow>(new CommandDefinition(AttentionQuery.Sources, cancellationToken: ct));
            var sources = sourceRows.ToList();
            var lastWorker = await db.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
                "SELECT max(last_seen_at) FROM factory.worker", cancellationToken: ct));
            var workerStaleAfter = TimeSpan.FromSeconds(Math.Max(factoryOptions.Value.PollingIntervalSeconds,
                factoryOptions.Value.LeaseHeartbeatSeconds) * 3);
            if (AttentionProjection.StaleWorker(lastWorker, now, workerStaleAfter) is { } worker) sources.Add(worker);
            var reviewLimit = factoryOptions.Value.MaxOutstandingReviewWork;
            var reviewCount = await db.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM factory.task WHERE status IN ('ReadyForPublish','Published')", cancellationToken: ct));
            if (reviewLimit > 0 && reviewCount >= reviewLimit)
                sources.Add(new AttentionSourceRow { Id = "global", Kind = "ReviewBacklog", Title = "Review backlog limit reached",
                    Reason = $"{reviewCount}/{reviewLimit} tasks await publication or merge.", FirstObservedAt = now, LastObservedAt = now });
            var items = AttentionProjection.Sort(AttentionProjection.ForTasks(taskRows, now, githubOptions.Value.MaxCiRepairAttempts)
                .Concat(AttentionProjection.ForSources(sources)));
            return Reply($"{items.Count} current attention signal(s) were found.",
                "These signals come from persisted task, CI, dispatch, quota, worker, review, and repository-sync state.",
                items.Count == 0 ? "No listed signal needs action now." : "Open the highest-ranked linked records first.", now,
                items.Take(8).Select(item => new OperatorEvidence(item.Kind, $"{item.Title}: {item.Reason}", item.Href))
                    .Append(new("Full attention feed", "Current dashboard evidence", "/")).ToArray());
        }
        if (intent == "next")
        {
            var next = await tasks.GetNextEligibleTaskAsync(ct);
            return next is null ? Reply("No task is currently eligible to claim.", null,
                "Inspect the task queue and attention feed for prerequisites or blocked providers.", now,
                [new("Task queue", "Pending tasks and dependencies", "/tasks")])
                : Reply($"Next eligible task: {next.Title} (priority {next.Priority}).", null,
                    "The worker makes the final claim after rechecking state.", now,
                    [new("Task", next.Title, $"/tasks/{next.TaskId}")]);
        }
        return Reply("I can answer from recorded factory state.", null,
            "Try: What is running? Why idle? What changed today? What needs me? What is next? Why did task <ID> retry? For controls, name one task ID or ask to pause or resume dispatch.",
            now);
    }

    private static async Task<bool> IsMergeReadyAsync(NpgsqlConnection db, Guid taskId, DateTimeOffset now, int maxCiRepairs, CancellationToken ct)
    {
        var row = (await db.QueryAsync<AttentionTaskRow>(new CommandDefinition(AttentionQuery.Tasks + " AND t.id=@taskId",
            new { taskId }, cancellationToken: ct))).SingleOrDefault();
        return row is not null && AttentionProjection.ForTasks([row], now, maxCiRepairs).Any(item => item.Action == "merge");
    }

    private static OperatorReply Reply(string observed, string? explanation, string? suggestion, DateTimeOffset now,
        IReadOnlyList<OperatorEvidence>? evidence = null, OperatorAction? action = null) =>
        new(observed, explanation, suggestion, evidence ?? [], action, now);

    private sealed class OperatorTaskRow { public Guid Id { get; init; } public string Title { get; init; } = ""; public string Status { get; init; } = ""; public bool RepairPaused { get; init; } public string RepositoryOwner { get; init; } = ""; public string RepositoryName { get; init; } = ""; public int? IssueNumber { get; init; } public string? PullRequestUrl { get; init; } }
    private sealed class OperatorEventRow { public string? FromStatus { get; init; } public string ToStatus { get; init; } = ""; public string? Reason { get; init; } public DateTimeOffset OccurredAt { get; init; } }
    private sealed class OperatorChangeRow { public Guid TaskId { get; init; } public string Title { get; init; } = ""; public string Status { get; init; } = ""; public string? Reason { get; init; } public DateTimeOffset OccurredAt { get; init; } }
}
