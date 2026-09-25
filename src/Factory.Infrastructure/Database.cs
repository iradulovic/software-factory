using Dapper;
using Factory.Core;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;

namespace Factory.Infrastructure;

public sealed class DatabaseMigrator(IOptions<FactoryOptions> options)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var root = FindRepositoryRoot();
        var files = Directory.GetFiles(Path.Combine(root, "database", "migrations"), "*.sql").Order(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("CREATE SCHEMA IF NOT EXISTS factory; CREATE TABLE IF NOT EXISTS factory.schema_migration(version TEXT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now());", cancellationToken: cancellationToken));
        foreach (var file in files)
        {
            var version = Path.GetFileName(file);
            if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM factory.schema_migration WHERE version=@version)", new { version }, cancellationToken: cancellationToken))) continue;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(await File.ReadAllTextAsync(file, cancellationToken), transaction: transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO factory.schema_migration(version) VALUES (@version) ON CONFLICT DO NOTHING", new { version }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SoftwareFactory.slnx"))) current = current.Parent;
        return current?.FullName ?? Directory.GetCurrentDirectory();
    }
}

internal sealed class TaskRow
{
    public Guid Id { get; init; }
    public long RepositoryId { get; init; }
    public long? GitHubIssueId { get; init; }
    public int? IssueNumber { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string TaskType { get; init; } = "";
    public int Priority { get; init; }
    public string Status { get; init; } = "";
    public string? PreferredAgent { get; init; }
    public string BaseBranch { get; init; } = "";
    public string? BranchName { get; init; }
    public string? WorktreePath { get; init; }
    public string? ClaimedBy { get; init; }
    public DateTime? ClaimedAt { get; init; }
    public DateTime? LeaseUntil { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime? FailedAt { get; init; }
    public string? FailureReason { get; init; }
    public string? ResumableSessionId { get; init; }
    public string? ResumableSessionAgent { get; init; }
    public string? PreferredAgentReason { get; init; }
    public string? AgentRoutingError { get; init; }

    public FactoryTask ToModel() => new(Id, RepositoryId, GitHubIssueId, IssueNumber, Title, Description, TaskType, Priority,
        Enum.Parse<FactoryTaskStatus>(Status), PreferredAgent, BaseBranch, BranchName, WorktreePath, ClaimedBy, Offset(ClaimedAt), Offset(LeaseUntil),
        Offset(CreatedAt), Offset(StartedAt), Offset(CompletedAt), Offset(FailedAt), FailureReason, ResumableSessionId, ResumableSessionAgent,
        PreferredAgentReason, AgentRoutingError);

    private static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? Offset(DateTime? value) => value is null ? null : Offset(value.Value);
}

internal sealed class TrackerFileTaskRow
{
    public Guid TaskId { get; init; }
    public string TrackerItemId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? FailureReason { get; init; }
    public string? WritebackSection { get; init; }

    public TrackerFileTask ToModel() => new(TaskId, TrackerItemId, Enum.Parse<FactoryTaskStatus>(Status), FailureReason,
        WritebackSection is null ? null : Enum.Parse<TrackerSection>(WritebackSection));
}

internal sealed class WorktreeCleanupCandidateRow
{
    public Guid TaskId { get; init; }
    public string Status { get; init; } = "";
    public string WorktreePath { get; init; } = "";
    public string RepositoryOwner { get; init; } = "";
    public string RepositoryName { get; init; } = "";
}

public sealed class PostgresTaskStore(IOptions<FactoryOptions> options, IClock clock) : ITaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<FactoryTaskStatus> CancellableRestingStatuses =
    [
        FactoryTaskStatus.Pending, FactoryTaskStatus.ReadyForPublish,
        FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed
    ];

    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    private static Task<bool> HasActiveManualMergeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid taskId, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM factory.manual_merge_request WHERE task_id=@taskId AND status IN ('Running','Succeeded'))",
            new { taskId }, transaction, cancellationToken: cancellationToken));

    public async Task<FactoryTask?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidate AS (
              SELECT id,status AS old_status,status <> 'Pending' AS recovered
              FROM factory.task
              WHERE (status='Pending' AND NOT repair_paused AND NOT EXISTS (
                  -- SF-611: a task with an unmerged prerequisite is never claimable, however high its priority —
                  -- only an already-executing recovery (below) skips this, since that task already started.
                  SELECT 1 FROM factory.task_dependency td JOIN factory.task dep ON dep.id=td.depends_on_task_id
                  WHERE td.task_id=factory.task.id AND dep.status <> 'Completed'
                )
                AND (@maxOutstandingReviewWork <= 0 OR (
                  -- SF-612: cap outstanding review work (validated-but-unpublished ReadyForPublish plus
                  -- already-published-awaiting-merge Published) so unattended implementation can never outrun
                  -- the operator's own review capacity. Only a brand-new Pending claim is gated — publication
                  -- and reconciliation, which free this count back up, are untouched, and a recovery below never
                  -- rechecks the limit since that task already started before it could matter.
                  SELECT count(*) FROM factory.task WHERE status IN ('ReadyForPublish','Published')
                ) < @maxOutstandingReviewWork))
                OR (status = ANY(@executingStatuses) AND NOT repair_paused AND lease_until < now())
              ORDER BY priority DESC, created_at FOR UPDATE SKIP LOCKED LIMIT 1
            ), failed_steps AS (
              UPDATE factory.step s
              SET status='Failed',completed_at=now(),
                duration_ms=GREATEST(0,CAST(EXTRACT(EPOCH FROM (now()-s.started_at))*1000 AS BIGINT)),
                error=COALESCE(s.error,'Worker lease expired; execution abandoned')
              FROM factory.run r,candidate c
              WHERE c.recovered AND r.task_id=c.id AND s.run_id=r.id AND s.status='Running'
            ), failed_runs AS (
              UPDATE factory.run r SET status='Failed',completed_at=now()
              FROM candidate c WHERE c.recovered AND r.task_id=c.id AND r.status='Running'
            ), claimed AS (
              UPDATE factory.task t SET status='Claimed',claimed_by=@workerId,claimed_at=now(),lease_until=now()+@lease,
                started_at=COALESCE(started_at,now()),failure_reason=NULL,current_agent=NULL,current_agent_reason=NULL
              FROM candidate c WHERE t.id=c.id
              RETURNING t.id,
                t.repository_id AS "RepositoryId",
                t.github_issue_id AS "GitHubIssueId",
                t.title,t.description,
                t.task_type AS "TaskType",
                t.priority,t.status,
                t.preferred_agent AS "PreferredAgent",
                t.preferred_agent_reason AS "PreferredAgentReason",
                t.agent_routing_error AS "AgentRoutingError",
                t.base_branch AS "BaseBranch",
                t.branch_name AS "BranchName",
                t.worktree_path AS "WorktreePath",
                t.claimed_by AS "ClaimedBy",
                t.claimed_at AS "ClaimedAt",
                t.lease_until AS "LeaseUntil",
                t.created_at AS "CreatedAt",
                t.started_at AS "StartedAt",
                t.completed_at AS "CompletedAt",
                t.failed_at AS "FailedAt",
                t.failure_reason AS "FailureReason",
                t.resumable_session_id AS "ResumableSessionId",
                t.resumable_session_agent AS "ResumableSessionAgent"
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT c.id,c.old_status,'Claimed',
                CASE WHEN c.recovered THEN 'Recovered from expired lease and claimed by ' || @workerId ELSE 'Claimed by ' || @workerId END,
                'orchestrator'
              FROM candidate c JOIN claimed cl ON cl.id=c.id
            )
            SELECT claimed.id,
              claimed."RepositoryId",
              claimed."GitHubIssueId",
              (SELECT issue_number FROM github.issue WHERE id=claimed."GitHubIssueId") AS "IssueNumber",
              claimed.title,claimed.description,
              claimed."TaskType",
              claimed.priority,claimed.status,
              claimed."PreferredAgent",
              claimed."PreferredAgentReason",
              claimed."AgentRoutingError",
              claimed."BaseBranch",
              claimed."BranchName",
              claimed."WorktreePath",
              claimed."ClaimedBy",
              claimed."ClaimedAt",
              claimed."LeaseUntil",
              claimed."CreatedAt",
              claimed."StartedAt",
              claimed."CompletedAt",
              claimed."FailedAt",
              claimed."FailureReason",
              claimed."ResumableSessionId",
              claimed."ResumableSessionAgent"
            FROM claimed;
            """;
        var executingStatuses = TaskStateMachine.ExecutingStatuses.Select(s => s.ToString()).ToList();
        await using var connection = Connection();
        var row = await connection.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(sql,
            new { workerId, lease, executingStatuses, maxOutstandingReviewWork = options.Value.MaxOutstandingReviewWork }, cancellationToken: cancellationToken));
        return row?.ToModel();
    }

    public async Task<bool> RenewLeaseAsync(Guid taskId, string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.task SET lease_until=now()+@lease
            WHERE id=@taskId AND claimed_by=@workerId AND lease_until >= now() AND status = ANY(@executingStatuses)
            """;
        var executingStatuses = TaskStateMachine.ExecutingStatuses.Select(s => s.ToString()).ToList();
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, new { taskId, workerId, lease, executingStatuses }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<bool> IsCancellationRequestedAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT status IN ('Stopping','Cancelled') FROM factory.task WHERE id=@taskId", new { taskId }, cancellationToken: cancellationToken));
    }

    public async Task<int> FinalizeExpiredCancellationsAsync(CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled);
        const string sql = """
            WITH candidates AS (
              SELECT id FROM factory.task
              WHERE status='Stopping' AND (lease_until IS NULL OR lease_until < now())
              FOR UPDATE SKIP LOCKED
            ), closed_steps AS (
              UPDATE factory.step s SET status='Cancelled',completed_at=now(),
                duration_ms=GREATEST(0,CAST(EXTRACT(EPOCH FROM (now()-s.started_at))*1000 AS BIGINT)),
                error=COALESCE(s.error,'Cancellation acknowledged after the worker lease expired')
              FROM factory.run r,candidates c
              WHERE r.task_id=c.id AND s.run_id=r.id AND s.status='Running'
            ), closed_runs AS (
              UPDATE factory.run r SET status='Cancelled',completed_at=now()
              FROM candidates c WHERE r.task_id=c.id AND r.status='Running'
            ), updated AS (
              UPDATE factory.task t SET status='Cancelled',failure_reason=COALESCE(failure_reason,'Cancelled by operator'),
                claimed_by=NULL,claimed_at=NULL,lease_until=NULL,current_agent=NULL,current_agent_reason=NULL
              FROM candidates c WHERE t.id=c.id AND t.status='Stopping'
              RETURNING t.id
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT id,'Stopping','Cancelled','Worker lease expired before it acknowledged cancellation','orchestrator' FROM updated
            )
            SELECT count(*)::int FROM updated
            """;
        await using var connection = Connection();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<bool> FinalizeCancellationAsync(Guid taskId, Guid runId, string reason, CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Stopping, FactoryTaskStatus.Cancelled);
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentText = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM factory.task WHERE id=@taskId FOR UPDATE", new { taskId }, transaction, cancellationToken: cancellationToken));
        if (currentText is null || !Enum.TryParse<FactoryTaskStatus>(currentText, out var current) ||
            !TaskStateMachine.IsCancellationRequested(current)) return false;

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.step s SET status='Cancelled',completed_at=now(),
              duration_ms=GREATEST(0,CAST(EXTRACT(EPOCH FROM (now()-s.started_at))*1000 AS BIGINT)),
              error=COALESCE(s.error,@reason)
            FROM factory.run r
            WHERE r.id=@runId AND r.task_id=@taskId AND s.run_id=r.id AND s.status='Running';
            UPDATE factory.run SET status='Cancelled',completed_at=now()
            WHERE id=@runId AND task_id=@taskId AND status='Running';
            """, new { taskId, runId, reason }, transaction, cancellationToken: cancellationToken));

        if (current == FactoryTaskStatus.Stopping)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                WITH updated AS (
                  UPDATE factory.task SET status='Cancelled',failure_reason='Cancelled by operator',
                    claimed_by=NULL,claimed_at=NULL,lease_until=NULL,current_agent=NULL,current_agent_reason=NULL
                  WHERE id=@taskId AND status='Stopping'
                  RETURNING id
                )
                INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
                SELECT id,'Stopping','Cancelled',@reason,'orchestrator' FROM updated
                """, new { taskId, reason }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE factory.task SET claimed_by=NULL,claimed_at=NULL,lease_until=NULL,current_agent=NULL,current_agent_reason=NULL
                WHERE id=@taskId AND status='Cancelled'
                """, new { taskId }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseLeaseAsync(Guid taskId, string workerId, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET lease_until=now() - interval '1 second' WHERE id=@taskId AND claimed_by=@workerId", new { taskId, workerId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> CreateForIssueIfEligibleAsync(GitHubIssue issue, string baseBranch, CancellationToken cancellationToken)
    {
        if (!issue.Labels.Contains("factory:ready", StringComparer.OrdinalIgnoreCase) || !issue.State.Equals("OPEN", StringComparison.OrdinalIgnoreCase)) return false;
        var route = CodexIssueRouter.Resolve(issue.Labels);
        const string sql = """
            INSERT INTO factory.task(id,repository_id,github_issue_id,title,description,status,preferred_agent,
              preferred_agent_reason,agent_routing_error,base_branch)
            VALUES(@id,@repositoryId,@issueId,@title,@body,'Pending',@preferredAgent,@preferredAgentReason,@agentRoutingError,@baseBranch)
            ON CONFLICT DO NOTHING;
            """;
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            id = Guid.NewGuid(), repositoryId = issue.RepositoryId, issueId = issue.Id, issue.Title, issue.Body, baseBranch,
            preferredAgent = route.PreferredAgent, preferredAgentReason = route.Reason, agentRoutingError = route.Error
        }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task TransitionAsync(Guid taskId, FactoryTaskStatus expected, FactoryTaskStatus next, string? failureReason, CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(expected, next);
        var completion = next == FactoryTaskStatus.Completed ? ", completed_at=now()" : next == FactoryTaskStatus.Failed ? ", failed_at=now()" : "";
        // Leaving active execution releases the worker's ownership right here, at the single application-level
        // transition boundary, so a resting task's now-meaningless lease can never make ClaimNextAsync mistake it
        // for an abandoned execution (this is what keeps a validated ReadyForPublish task, for example, from being
        // implemented again while it waits for a human to publish it).
        var releaseOwnership = TaskStateMachine.RetainsWorkerOwnership(next) ? "" : ", claimed_by=NULL, claimed_at=NULL, lease_until=NULL";
        var sql = $"""
            WITH updated AS (
              UPDATE factory.task SET status=@next, failure_reason=@failureReason{completion}{releaseOwnership}, current_agent=NULL,current_agent_reason=NULL WHERE id=@taskId AND status=@expected
              RETURNING id
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT id,@expected,@next,@failureReason,'orchestrator' FROM updated
            )
            SELECT count(*)::int FROM updated
            """;
        await using var connection = Connection();
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { taskId, expected = expected.ToString(), next = next.ToString(), failureReason }, cancellationToken: cancellationToken));
        if (count != 1) throw new InvalidOperationException($"Task {taskId} was not in expected state {expected}.");
    }

    public Task<bool> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        TransitionFromCurrentAsync(taskId, [FactoryTaskStatus.Failed, FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Rejected], FactoryTaskStatus.Pending, true, "Retried by operator", cancellationToken);

    public async Task<bool> SetRepairPausedAsync(Guid taskId, bool paused, string actor, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await connection.QuerySingleOrDefaultAsync<TaskRepairPauseRow>(new CommandDefinition(
            "SELECT status,repair_paused AS \"RepairPaused\" FROM factory.task WHERE id=@taskId FOR UPDATE",
            new { taskId }, transaction, cancellationToken: cancellationToken));
        if (current is null || !Enum.TryParse<FactoryTaskStatus>(current.Status, out var status) ||
            !TaskStateMachine.CanPauseRepairs(status)) return false;
        if (current.RepairPaused == paused) return false;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET repair_paused=@paused WHERE id=@taskId",
            new { taskId, paused }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
            VALUES(@taskId,@status,@status,@reason,@actor)
            """, new { taskId, status = current.Status,
                reason = paused ? "Operator stopped further automatic attempts after current execution" : "Operator resumed automatic attempts",
                actor }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<TaskCancellationOutcome> CancelAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentText = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM factory.task WHERE id=@taskId FOR UPDATE", new { taskId }, transaction, cancellationToken: cancellationToken));
        if (currentText is null || !Enum.TryParse<FactoryTaskStatus>(currentText, out var current))
            return TaskCancellationOutcome.NotCancellable;
        if (current == FactoryTaskStatus.Stopping) return TaskCancellationOutcome.Stopping;
        if (current == FactoryTaskStatus.Cancelled) return TaskCancellationOutcome.Cancelled;

        if (current == FactoryTaskStatus.ReadyForPublish)
        {
            var publicationInFlight = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(
                  SELECT 1 FROM factory.publication WHERE task_id=@taskId AND status IN ('Publishing','PullRequestCreated'))
                """, new { taskId }, transaction, cancellationToken: cancellationToken));
            if (publicationInFlight) return TaskCancellationOutcome.NotCancellable;
        }

        var isExecuting = TaskStateMachine.ExecutingStatuses.Contains(current);
        if (!isExecuting && !CancellableRestingStatuses.Contains(current))
            return TaskCancellationOutcome.NotCancellable;

        var next = isExecuting ? FactoryTaskStatus.Stopping : FactoryTaskStatus.Cancelled;
        TaskStateMachine.EnsureCanTransition(current, next);
        var ownership = next == FactoryTaskStatus.Stopping
            ? ""
            : ", claimed_by=NULL, claimed_at=NULL, lease_until=NULL";
        var reason = next == FactoryTaskStatus.Stopping ? "Cancellation requested by operator" : "Cancelled by operator";
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition($"""
            WITH updated AS (
              UPDATE factory.task SET status=@next,failure_reason=@reason{ownership},current_agent=CASE WHEN @next='Cancelled' THEN NULL ELSE current_agent END
              WHERE id=@taskId AND status=@current
              RETURNING id
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT id,@current,@next,@reason,'human' FROM updated
            )
            SELECT count(*)::int FROM updated
            """, new { taskId, current = current.ToString(), next = next.ToString(), reason }, transaction, cancellationToken: cancellationToken));
        if (count != 1) throw new InvalidOperationException($"Task {taskId} was not in expected state {current}.");
        if (next == FactoryTaskStatus.Cancelled)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE factory.publication SET status='Cancelled',completed_at=now(),lease_until=NULL,
                  error=COALESCE(error,'Task cancelled before publication was claimed')
                WHERE task_id=@taskId AND status='Requested'
                """, new { taskId }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return next == FactoryTaskStatus.Stopping ? TaskCancellationOutcome.Stopping : TaskCancellationOutcome.Cancelled;
    }

    private static readonly IReadOnlyCollection<FactoryTaskStatus> ContinuableStatuses =
    [
        FactoryTaskStatus.Failed, FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Rejected,
        FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published
    ];

    public async Task<bool> ContinueWithFeedbackAsync(Guid taskId, string feedback, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentText = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM factory.task WHERE id=@taskId FOR UPDATE", new { taskId }, transaction, cancellationToken: cancellationToken));
        if (currentText is null) return false;
        if (await HasActiveManualMergeAsync(connection, transaction, taskId, cancellationToken)) return false;
        var current = Enum.Parse<FactoryTaskStatus>(currentText);
        if (!ContinuableStatuses.Contains(current)) return false;
        TaskStateMachine.EnsureCanTransition(current, FactoryTaskStatus.Pending);

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO factory.task_feedback(id,task_id,body,created_by) VALUES(@id,@taskId,@feedback,'operator')",
            new { id = Guid.NewGuid(), taskId, feedback }, transaction, cancellationToken: cancellationToken));
        // The existing branch/worktree are deliberately left untouched — the whole point is to keep working on
        // the same changes, not start over, so a later publish updates the same pull request instead of a new one.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.task SET status='Pending',claimed_by=NULL,claimed_at=NULL,lease_until=NULL,failure_reason=NULL,failed_at=NULL,completed_at=NULL WHERE id=@taskId",
            new { taskId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor) VALUES(@taskId,@current,'Pending','Continued with operator feedback','human')",
            new { taskId, current = current.ToString() }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<TaskFeedback>> GetFeedbackAsync(Guid taskId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id AS "Id",task_id AS "TaskId",body AS "Body",created_at AS "CreatedAt",created_by AS "CreatedBy"
            FROM factory.task_feedback WHERE task_id=@taskId ORDER BY created_at
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<TaskFeedbackRow>(new CommandDefinition(sql, new { taskId }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<bool> CancelPendingForIssueAsync(long issueId, string reason, CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Pending, FactoryTaskStatus.Cancelled);
        const string sql = """
            WITH updated AS (
              UPDATE factory.task SET status='Cancelled',claimed_by=NULL,claimed_at=NULL,lease_until=NULL
              WHERE github_issue_id=@issueId AND status='Pending'
              RETURNING id
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT id,'Pending','Cancelled',@reason,'orchestrator' FROM updated
            )
            SELECT count(*)::int FROM updated
            """;
        await using var connection = Connection();
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { issueId, reason }, cancellationToken: cancellationToken));
        return count > 0;
    }

    public async Task RecordHeartbeatAsync(string workerId, string host, Guid? currentTaskId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.worker(worker_id,host,last_seen_at,current_task_id)
            VALUES(@workerId,@host,now(),@currentTaskId)
            ON CONFLICT(worker_id) DO UPDATE SET host=excluded.host,last_seen_at=excluded.last_seen_at,current_task_id=excluded.current_task_id
            """;
        await using var connection = Connection();
        await connection.ExecuteAsync(new CommandDefinition(sql, new { workerId, host, currentTaskId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<WorktreeCleanupCandidate>> GetWorktreeCleanupCandidatesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.id AS "TaskId",t.status AS "Status",t.worktree_path AS "WorktreePath",
              gr.owner AS "RepositoryOwner",gr.name AS "RepositoryName"
            FROM factory.task t JOIN github.repository gr ON gr.id=t.repository_id
            WHERE t.worktree_path IS NOT NULL AND t.status = ANY(@eligibleStatuses)
            """;
        var eligibleStatuses = WorktreeCleanupPolicy.EligibleStatuses.Select(s => s.ToString()).ToList();
        await using var connection = Connection();
        var rows = await connection.QueryAsync<WorktreeCleanupCandidateRow>(new CommandDefinition(sql, new { eligibleStatuses }, cancellationToken: cancellationToken));
        return rows.Select(row => new WorktreeCleanupCandidate(row.TaskId, Enum.Parse<FactoryTaskStatus>(row.Status), row.WorktreePath, row.RepositoryOwner, row.RepositoryName)).ToList();
    }

    public async Task<bool> ClearWorkspaceIfStatusUnchangedAsync(Guid taskId, FactoryTaskStatus expectedStatus, CancellationToken cancellationToken)
    {
        const string sql = "UPDATE factory.task SET worktree_path=NULL,branch_name=NULL WHERE id=@taskId AND status=@status";
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, new { taskId, status = expectedStatus.ToString() }, cancellationToken: cancellationToken)) == 1;
    }

    private async Task<bool> TransitionFromCurrentAsync(Guid taskId, IReadOnlyCollection<FactoryTaskStatus> allowedSources, FactoryTaskStatus next, bool resetExecution, string reason, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentText = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT status FROM factory.task WHERE id=@taskId FOR UPDATE", new { taskId }, transaction, cancellationToken: cancellationToken));
        if (currentText is null) return false;
        if (await HasActiveManualMergeAsync(connection, transaction, taskId, cancellationToken)) return false;
        var current = Enum.Parse<FactoryTaskStatus>(currentText);
        if (!allowedSources.Contains(current)) return false;
        TaskStateMachine.EnsureCanTransition(current, next);
        var sql = resetExecution
            ? "UPDATE factory.task SET status=@next,claimed_by=NULL,claimed_at=NULL,lease_until=NULL,failure_reason=NULL,failed_at=NULL,completed_at=NULL WHERE id=@taskId"
            : "UPDATE factory.task SET status=@next,claimed_by=NULL,claimed_at=NULL,lease_until=NULL WHERE id=@taskId";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { taskId, next = next.ToString() }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor) VALUES(@taskId,@current,@next,@reason,'human')",
            new { taskId, current = current.ToString(), next = next.ToString(), reason }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task SetWorkspaceAsync(Guid taskId, string branchName, string worktreePath, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET branch_name=@branchName,worktree_path=@worktreePath WHERE id=@taskId", new { taskId, branchName, worktreePath }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> StartRunAsync(Guid taskId, string workerId, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid(); await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO factory.run(id,task_id,started_at,status,worker_id) VALUES(@id,@taskId,@now,'Running',@workerId)", new { id, taskId, now = clock.UtcNow, workerId }, cancellationToken: cancellationToken)); return id;
    }

    public async Task<Guid> StartStepAsync(Guid runId, string stepType, int attempt, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var logPath = StepLogPaths.Resolve(options.Value.LogsDirectory, runId, id);
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO factory.step(id,run_id,step_type,status,started_at,attempt,log_path) VALUES(@id,@runId,@stepType,'Running',@now,@attempt,@logPath)", new { id, runId, stepType, now = clock.UtcNow, attempt, logPath }, cancellationToken: cancellationToken));
        return id;
    }

    public async Task CompleteStepAsync(Guid stepId, ExecutionStatus status, string? error, string? output, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.step s SET status=@status,completed_at=@now,
              duration_ms=CAST(EXTRACT(EPOCH FROM (@now-s.started_at))*1000 AS BIGINT),error=@error,output=@output
            WHERE s.id=@stepId AND s.status='Running' AND EXISTS (
              SELECT 1 FROM factory.run r JOIN factory.task t ON t.id=r.task_id
              WHERE r.id=s.run_id AND t.status NOT IN ('Stopping','Cancelled'))
            """;
        await using var c = Connection(); await c.ExecuteAsync(new CommandDefinition(sql, new { stepId, status = status.ToString(), now = clock.UtcNow, error, output }, cancellationToken: cancellationToken));
    }

    public async Task SaveAgentRunAsync(AgentRunRecord r, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.agent_run(
              id,task_id,run_id,step_id,agent,started_at,completed_at,duration_seconds,exit_code,status,stdout,stderr,
              quota_detected,quota_reset_at,attempt_number,needs_human,counts_as_implementation_attempt,provider_session_id,
              model,reasoning_effort,selection_reason,purpose,
              result_json,result_summary,tests_run,tests_passed,files_changed,risks,human_reason)
            VALUES(
              @Id,@TaskId,@RunId,@StepId,@Agent,@StartedAt,@CompletedAt,@DurationSeconds,@ExitCode,@Status,@StandardOutput,@StandardError,
              @QuotaDetected,@QuotaResetAt,@AttemptNumber,@NeedsHuman,@CountsAsImplementationAttempt,@ProviderSessionId,
              @Model,@ReasoningEffort,@SelectionReason,@Purpose,
              CAST(@ResultJson AS jsonb),@ResultSummary,CAST(@TestsRun AS jsonb),@TestsPassed,
              CAST(@FilesChanged AS jsonb),CAST(@Risks AS jsonb),@HumanReason)
            """;
        var parameters = new
        {
            r.Id, r.TaskId, r.RunId, r.StepId, r.Agent, r.StartedAt, r.CompletedAt, r.DurationSeconds, r.ExitCode, r.Status,
            r.StandardOutput, r.StandardError, r.QuotaDetected, r.QuotaResetAt, r.AttemptNumber, r.NeedsHuman, r.CountsAsImplementationAttempt,
            r.ProviderSessionId, r.Model, r.ReasoningEffort, r.SelectionReason, r.Purpose,
            ResultJson = r.Result is null ? null : JsonSerializer.Serialize(r.Result, JsonOptions),
            ResultSummary = r.Result?.Summary,
            TestsRun = r.Result is null ? null : JsonSerializer.Serialize(r.Result.TestsRun, JsonOptions),
            TestsPassed = r.Result?.TestsPassed,
            FilesChanged = r.Result is null ? null : JsonSerializer.Serialize(r.Result.FilesChanged, JsonOptions),
            Risks = r.Result is null ? null : JsonSerializer.Serialize(r.Result.Risks, JsonOptions),
            HumanReason = r.Result?.HumanReason
        };
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    public async Task CompleteRunAsync(Guid runId, ExecutionStatus status, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.run r SET status=@status,completed_at=@now
            WHERE r.id=@runId AND r.status='Running' AND EXISTS (
              SELECT 1 FROM factory.task t WHERE t.id=r.task_id AND t.status NOT IN ('Stopping','Cancelled'))
            """;
        await using var c = Connection(); await c.ExecuteAsync(new CommandDefinition(sql, new { runId, status = status.ToString(), now = clock.UtcNow }, cancellationToken: cancellationToken));
    }

    public async Task SetRunConfigurationAsync(Guid runId, RepositoryConfiguration configuration, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.run SET repository_configuration=CAST(@json AS jsonb) WHERE id=@runId",
            new { runId, json = JsonSerializer.Serialize(configuration, JsonOptions) }, cancellationToken: cancellationToken));
    }

    public async Task CloseExecutionAsync(Guid runId, ExecutionStatus status, string reason, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.step SET status=@status,completed_at=@now,
              duration_ms=GREATEST(0,CAST(EXTRACT(EPOCH FROM (@now-started_at))*1000 AS BIGINT)),error=COALESCE(error,@reason)
            WHERE run_id=@runId AND status='Running';
            UPDATE factory.run SET status=@status,completed_at=@now WHERE id=@runId AND status='Running';
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new { runId, status = status.ToString(), now = clock.UtcNow, reason }, cancellationToken: cancellationToken));
    }

    public async Task SetChangeSummaryAsync(Guid runId, ChangeSummary summary, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.run SET base_commit=@BaseCommit,head_commit=@HeadCommit,files_changed=@FilesChanged,
              lines_added=@LinesAdded,lines_removed=@LinesRemoved
            WHERE id=@runId
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new
        {
            runId, summary.BaseCommit, summary.HeadCommit, FilesChanged = summary.FilesChanged.ToArray(), summary.LinesAdded, summary.LinesRemoved
        }, cancellationToken: cancellationToken));
    }

    public async Task<Guid?> RequestPublicationAsync(Guid taskId, Guid? runId, string requestedBy, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.OpenAsync(cancellationToken);
        await using var transaction = await c.BeginTransactionAsync(cancellationToken);
        var status = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM factory.task WHERE id=@taskId FOR UPDATE", new { taskId }, transaction, cancellationToken: cancellationToken));
        if (status != nameof(FactoryTaskStatus.ReadyForPublish)) return null;

        var id = await c.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO factory.publication(id,task_id,run_id,status,requested_by)
            VALUES(@id,@taskId,@runId,'Requested',@requestedBy)
            ON CONFLICT (task_id) WHERE status IN ('Requested','Publishing') DO NOTHING
            RETURNING id
            """, new { id = Guid.NewGuid(), taskId, runId, requestedBy }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<PublicationRequest?> ClaimNextPublicationAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        // A 'Publishing' row whose lease has expired means the worker that claimed it crashed before recording
        // completion (push, pull-request creation, and completing the publication record are all idempotent under
        // retry, so reclaiming it here is always safe); a freshly claimed row gets its own lease so a still-live
        // worker's own in-flight attempt is never reclaimed out from under it.
        const string claimSql = """
            UPDATE factory.publication SET status='Publishing',claimed_by=@workerId,claimed_at=now(),lease_until=now()+@lease
            WHERE id = (
              SELECT p.id FROM factory.publication p JOIN factory.task t ON t.id=p.task_id
              WHERE (p.status='Requested' OR (p.status='Publishing' AND p.lease_until < now())) AND t.status='ReadyForPublish'
              ORDER BY p.requested_at FOR UPDATE OF p,t SKIP LOCKED LIMIT 1
            )
            RETURNING id,task_id AS "TaskId"
            """;
        await using var c = Connection();
        var claimed = await c.QuerySingleOrDefaultAsync<PublicationClaimRow>(new CommandDefinition(claimSql, new { workerId, lease }, cancellationToken: cancellationToken));
        if (claimed is null) return null;

        const string detailSql = """
            SELECT t.branch_name AS "BranchName",t.worktree_path AS "WorktreePath",t.base_branch AS "BaseBranch",
              t.validated_head_commit AS "ValidatedHeadCommit",t.require_human_merge AS "RequireHumanMerge",
              r.id AS "RepositoryId",r.owner AS "RepositoryOwner",r.name AS "RepositoryName",
              t.title AS "TaskTitle",i.issue_number AS "IssueNumber"
            FROM factory.task t
            JOIN github.repository r ON r.id=t.repository_id
            LEFT JOIN github.issue i ON i.id=t.github_issue_id
            WHERE t.id=@taskId
            """;
        var details = await c.QuerySingleAsync<PublicationDetailsRow>(new CommandDefinition(detailSql, new { taskId = claimed.TaskId }, cancellationToken: cancellationToken));
        if (details.BranchName is null || details.WorktreePath is null)
            throw new InvalidOperationException($"Task {claimed.TaskId} has no recorded worktree; it cannot be published.");
        return new PublicationRequest(claimed.Id, claimed.TaskId, details.BranchName, details.WorktreePath, details.BaseBranch,
            details.RepositoryId, details.RepositoryOwner, details.RepositoryName, details.TaskTitle, details.IssueNumber, details.ValidatedHeadCommit,
            details.RequireHumanMerge);
    }

    public async Task CompletePublicationAsync(Guid publicationId, string status, int? pullRequestNumber, string? pullRequestUrl, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.publication SET status=@status,pull_request_number=@pullRequestNumber,pull_request_url=@pullRequestUrl,error=@error,completed_at=now(),lease_until=NULL
            WHERE id=@publicationId
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new { publicationId, status, pullRequestNumber, pullRequestUrl, error }, cancellationToken: cancellationToken));
    }

    public async Task<int> ReconcilePublishedTasksAsync(CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published);
        // A task can be stuck resting in ReadyForPublish even though its publication already succeeded, if the
        // worker crashed between recording that success (CompletePublicationAsync) and making this transition.
        // Finding it this way, from the publication's own recorded outcome, needs no separate flag on the task.
        const string sql = """
            WITH latest AS (
              SELECT DISTINCT ON (task_id) task_id, status
              FROM factory.publication
              ORDER BY task_id, requested_at DESC
            ), stuck AS (
              SELECT t.id FROM factory.task t
              JOIN latest l ON l.task_id = t.id
              WHERE t.status = 'ReadyForPublish' AND l.status = 'PullRequestCreated'
            ), updated AS (
              UPDATE factory.task t SET status='Published',claimed_by=NULL,claimed_at=NULL,lease_until=NULL
              FROM stuck s WHERE t.id = s.id
              RETURNING t.id
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT id,'ReadyForPublish','Published',
                'Recovered: the pull request was already recorded but the task transition had not completed.','orchestrator'
              FROM updated
            )
            SELECT count(*)::int FROM updated
            """;
        await using var c = Connection();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task SetValidatedHeadCommitAsync(Guid taskId, string headCommit, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET validated_head_commit=@headCommit WHERE id=@taskId", new { taskId, headCommit }, cancellationToken: cancellationToken));
    }

    public async Task SetRequireHumanMergeAsync(Guid taskId, bool requireHumanMerge, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET require_human_merge=@requireHumanMerge WHERE id=@taskId", new { taskId, requireHumanMerge }, cancellationToken: cancellationToken));
    }

    public async Task SetReviewRequestedAsync(Guid taskId, bool requested, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET review_requested=@requested WHERE id=@taskId", new { taskId, requested }, cancellationToken: cancellationToken));
    }

    public async Task SaveReviewFindingsAsync(Guid taskId, Guid runId, string agent, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken)
    {
        if (findings.Count == 0) return;
        const string sql = """
            INSERT INTO factory.review_finding(id,task_id,run_id,agent,severity,file,line,description)
            VALUES(@Id,@TaskId,@RunId,@Agent,@Severity,@File,@Line,@Description)
            """;
        var rows = findings.Select(f => new { Id = Guid.NewGuid(), TaskId = taskId, RunId = runId, Agent = agent, f.Severity, f.File, f.Line, f.Description });
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, rows, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PersistedReviewFinding>> GetReviewFindingsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id AS "Id",task_id AS "TaskId",run_id AS "RunId",agent AS "Agent",severity AS "Severity",
              file AS "File",line AS "Line",description AS "Description",created_at AS "CreatedAt"
            FROM factory.review_finding WHERE task_id=@taskId ORDER BY created_at
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<ReviewFindingRow>(new CommandDefinition(sql, new { taskId }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SetCurrentAgentAsync(Guid taskId, string? agentName, string? selectionReason, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET current_agent=@agentName,current_agent_reason=@selectionReason WHERE id=@taskId", new { taskId, agentName, selectionReason }, cancellationToken: cancellationToken));
    }

    public async Task SetResumableSessionAsync(Guid taskId, string agentName, string? sessionId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.task SET resumable_session_id=@sessionId, resumable_session_agent=@agentName WHERE id=@taskId",
            new { taskId, agentName, sessionId }, cancellationToken: cancellationToken));
    }

    public async Task RecordGitHubWriteAsync(Guid taskId, string kind, string detail, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO factory.github_write(id,task_id,kind,detail,succeeded,error) VALUES(@id,@taskId,@kind,@detail,@succeeded,@error)";
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new { id = Guid.NewGuid(), taskId, kind, detail, succeeded, error }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PublishedTaskRef>> GetPublishedTasksAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.id AS "TaskId", r.owner AS "RepositoryOwner", r.name AS "RepositoryName", p.pull_request_number AS "PullRequestNumber",
              t.require_human_merge AS "RequireHumanMerge", t.status AS "Status"
            FROM factory.task t
            JOIN github.repository r ON r.id = t.repository_id
            JOIN LATERAL (
              SELECT pull_request_number FROM factory.publication
              WHERE task_id = t.id AND status = 'PullRequestCreated' AND pull_request_number IS NOT NULL
              ORDER BY completed_at DESC LIMIT 1
            ) p ON true
            WHERE t.status = 'Published' OR (t.status = 'NeedsHuman' AND (t.failure_reason LIKE 'Automatic merge%' OR t.failure_reason LIKE 'On-demand merge failed:%'))
            """;
        await using var c = Connection();
        return (await c.QueryAsync<PublishedTaskRef>(new CommandDefinition(sql, cancellationToken: cancellationToken))).AsList();
    }

    public async Task SetCiStatusAsync(Guid taskId, string overallStatus, string? headCommit, IReadOnlyList<PullRequestCheck> checks, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.task_ci_status(task_id,head_commit,overall_status,checks_json,error,synced_at)
            VALUES(@taskId,@headCommit,@overallStatus,@checksJson,@error,@syncedAt)
            ON CONFLICT(task_id) DO UPDATE SET
              head_commit=excluded.head_commit, overall_status=excluded.overall_status,
              checks_json=excluded.checks_json, error=excluded.error, synced_at=excluded.synced_at
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new
        {
            taskId, headCommit, overallStatus, checksJson = JsonSerializer.Serialize(checks), error, syncedAt = clock.UtcNow
        }, cancellationToken: cancellationToken));
    }

    public async Task SetMergeStatusAsync(Guid taskId, PullRequestMergeResult result, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.task_merge_status(task_id,status,head_sha,base_sha,mergeable,merge_state_status,error,synced_at)
            VALUES(@taskId,@status,@headSha,@baseSha,@mergeable,@mergeStateStatus,@error,@syncedAt)
            ON CONFLICT(task_id) DO UPDATE SET status=excluded.status,head_sha=excluded.head_sha,
              base_sha=excluded.base_sha,mergeable=excluded.mergeable,merge_state_status=excluded.merge_state_status,
              error=excluded.error,synced_at=excluded.synced_at
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new { taskId, status = result.Status, result.HeadSha, result.BaseSha,
            result.Mergeable, result.MergeStateStatus, result.Error, syncedAt = clock.UtcNow }, cancellationToken: cancellationToken));
    }

    public async Task<TaskMergeStatus?> GetMergeStatusAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<TaskMergeStatusRow>(new CommandDefinition("""
            SELECT task_id AS "TaskId",status,head_sha AS "HeadSha",base_sha AS "BaseSha",mergeable,
              merge_state_status AS "MergeStateStatus",error,synced_at AS "SyncedAt"
            FROM factory.task_merge_status WHERE task_id=@taskId
            """, new { taskId }, cancellationToken: cancellationToken));
        return row?.ToModel();
    }

    public async Task ClearMergeStatusAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("DELETE FROM factory.task_merge_status WHERE task_id=@taskId", new { taskId }, cancellationToken: cancellationToken));
    }

    public async Task<ManualMergeRequest?> BeginManualMergeAsync(Guid taskId, string requester, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ManualMergeCandidateRow>(new CommandDefinition("""
            SELECT t.status,t.failure_reason AS "FailureReason",t.branch_name AS "BranchName",
              t.validated_head_commit AS "ValidatedHeadCommit",r.owner AS "RepositoryOwner",r.name AS "RepositoryName",
              p.pull_request_number AS "PullRequestNumber"
            FROM factory.task t JOIN github.repository r ON r.id=t.repository_id
            LEFT JOIN LATERAL (
              SELECT pull_request_number FROM factory.publication
              WHERE task_id=t.id AND status='PullRequestCreated' AND pull_request_number IS NOT NULL
              ORDER BY completed_at DESC LIMIT 1
            ) p ON true
            WHERE t.id=@taskId FOR UPDATE OF t
            """, new { taskId }, transaction, cancellationToken: cancellationToken));
        if (row is null || (row.Status != "Published" && !(row.Status == "NeedsHuman" &&
            (row.FailureReason?.StartsWith("Automatic merge", StringComparison.Ordinal) == true ||
             row.FailureReason?.StartsWith("On-demand merge failed:", StringComparison.Ordinal) == true)))
            || row.PullRequestNumber is null || string.IsNullOrWhiteSpace(row.BranchName) || string.IsNullOrWhiteSpace(row.ValidatedHeadCommit)) return null;

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.manual_merge_request SET status='Failed',completed_at=now(),error='Request expired before an outcome was recorded; check GitHub before retrying.'
            WHERE task_id=@taskId AND status='Running' AND requested_at < now()-interval '5 minutes'
            """, new { taskId }, transaction, cancellationToken: cancellationToken));
        var requestId = Guid.NewGuid();
        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.manual_merge_request(id,task_id,requested_by,status)
            VALUES(@requestId,@taskId,@requester,'Running') ON CONFLICT DO NOTHING
            """, new { requestId, taskId, requester }, transaction, cancellationToken: cancellationToken));
        if (inserted != 1) return null;
        await transaction.CommitAsync(cancellationToken);
        return new ManualMergeRequest(requestId, taskId, row.RepositoryOwner, row.RepositoryName,
            row.PullRequestNumber.Value, row.BranchName!, row.ValidatedHeadCommit!);
    }

    public async Task CompleteManualMergeAsync(Guid requestId, bool succeeded, string? headSha, string? error, bool githubRejected, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ManualMergeOutcomeRow>(new CommandDefinition(
            "SELECT task_id AS \"TaskId\",status FROM factory.manual_merge_request WHERE id=@requestId FOR UPDATE",
            new { requestId }, transaction, cancellationToken: cancellationToken));
        if (row is null || row.Status != "Running") return;
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.manual_merge_request SET status=@status,completed_at=now(),head_sha=@headSha,error=@error WHERE id=@requestId
            """, new { requestId, status = succeeded ? "Succeeded" : "Failed", headSha, error }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
            SELECT t.id,t.status,t.status,@reason,'operator' FROM factory.task t WHERE t.id=@taskId
            """, new { taskId = row.TaskId, reason = succeeded ? $"On-demand merge accepted for {headSha}" : $"On-demand merge failed: {error}" }, transaction, cancellationToken: cancellationToken));
        if (githubRejected && !succeeded)
        {
            TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Published, FactoryTaskStatus.NeedsHuman);
            var moved = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE factory.task SET status='NeedsHuman',failure_reason=@reason
                WHERE id=@taskId AND status='Published' AND NOT require_human_merge
                """, new { taskId = row.TaskId, reason = $"On-demand merge failed: {error}" }, transaction, cancellationToken: cancellationToken));
            if (moved == 1)
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
                    VALUES(@taskId,'Published','NeedsHuman',@reason,'operator')
                    """, new { taskId = row.TaskId, reason = $"On-demand merge failed: {error}" }, transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TaskCiStatus?> GetCiStatusAsync(Guid taskId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT task_id AS "TaskId", overall_status AS "OverallStatus", head_commit AS "HeadCommit",
              checks_json AS "ChecksJson", error AS "Error", synced_at AS "SyncedAt",
              repair_triggered_for_commit AS "RepairTriggeredForCommit"
            FROM factory.task_ci_status WHERE task_id=@taskId
            """;
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<TaskCiStatusRow>(new CommandDefinition(sql, new { taskId }, cancellationToken: cancellationToken));
        return row?.ToModel();
    }

    /// <summary>Automatically continues a task whose published pull request's CI failed on its exact current head
    /// commit (SF-706) — the same <see cref="FactoryTaskStatus.Published"/>→<see cref="FactoryTaskStatus.Pending"/>
    /// path <see cref="ContinueWithFeedbackAsync"/> uses for a human continuation (so it grants the same fresh,
    /// bounded <c>MaxImplementationAttempts</c> allowance and reuses the existing branch/worktree unchanged), but
    /// attributed to the orchestrator (<c>task_feedback.created_by='ci-repair'</c>, <c>task_event.actor='orchestrator'</c>)
    /// rather than a human, and restricted to a task actually resting in <see cref="FactoryTaskStatus.Published"/>
    /// (never any of <see cref="ContinueWithFeedbackAsync"/>'s other continuable statuses — this is only ever
    /// called from the CI-status sync path). Also records <paramref name="headCommit"/> as the commit this repair
    /// was triggered for, in the same transaction as the feedback insert and the state transition, so a concurrent
    /// or repeated call for the same still-failing commit can never trigger a second repair for it.</summary>
    public async Task<bool> TriggerCiRepairAsync(Guid taskId, string headCommit, string feedback, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentText = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM factory.task WHERE id=@taskId AND NOT repair_paused FOR UPDATE", new { taskId }, transaction, cancellationToken: cancellationToken));
        if (currentText != nameof(FactoryTaskStatus.Published)) return false;
        if (await HasActiveManualMergeAsync(connection, transaction, taskId, cancellationToken)) return false;
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Published, FactoryTaskStatus.Pending);

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO factory.task_feedback(id,task_id,body,created_by) VALUES(@id,@taskId,@feedback,'ci-repair')",
            new { id = Guid.NewGuid(), taskId, feedback }, transaction, cancellationToken: cancellationToken));
        // The existing branch/worktree are deliberately left untouched, exactly as ContinueWithFeedbackAsync does —
        // the next attempt keeps working on the same changes, so a later publish updates the same pull request.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.task SET status='Pending',claimed_by=NULL,claimed_at=NULL,lease_until=NULL,failure_reason=NULL,failed_at=NULL,completed_at=NULL WHERE id=@taskId",
            new { taskId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor) VALUES(@taskId,'Published','Pending',@feedback,'orchestrator')",
            new { taskId, feedback }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.task_ci_status SET repair_triggered_for_commit=@headCommit WHERE task_id=@taskId",
            new { taskId, headCommit }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<long>> GetIngestedReviewCommentIdsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var rows = await c.QueryAsync<long>(new CommandDefinition(
            "SELECT comment_id FROM factory.task_review_comment_ingested WHERE task_id=@taskId", new { taskId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<int> IngestReviewFeedbackAsync(Guid taskId, IReadOnlyList<PullRequestFeedbackItem> comments, CancellationToken cancellationToken)
    {
        if (comments.Count == 0) return 0;

        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var currentText = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM factory.task WHERE id=@taskId FOR UPDATE", new { taskId }, transaction, cancellationToken: cancellationToken));
        if (currentText != nameof(FactoryTaskStatus.Published)) return 0;
        if (await HasActiveManualMergeAsync(connection, transaction, taskId, cancellationToken)) return 0;

        // Dedup is the source of truth, not the caller's own pre-filtering: only a comment id that actually
        // inserts here (ON CONFLICT DO NOTHING) is newly ingested, so a duplicate call for the same comment can
        // never apply its feedback twice.
        var newlyIngested = new List<PullRequestFeedbackItem>();
        foreach (var comment in comments)
        {
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO factory.task_review_comment_ingested(task_id,comment_id,author) VALUES(@taskId,@commentId,@author) ON CONFLICT DO NOTHING",
                new { taskId, commentId = comment.CommentId, author = comment.Author }, transaction, cancellationToken: cancellationToken));
            if (inserted > 0) newlyIngested.Add(comment);
        }
        if (newlyIngested.Count == 0) { await transaction.CommitAsync(cancellationToken); return 0; }

        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Published, FactoryTaskStatus.Pending);
        foreach (var comment in newlyIngested)
        {
            var feedback = $"Reviewer {comment.Author} ({comment.Kind}): {comment.Body}";
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO factory.task_feedback(id,task_id,body,created_by) VALUES(@id,@taskId,@feedback,'review-comment')",
                new { id = Guid.NewGuid(), taskId, feedback }, transaction, cancellationToken: cancellationToken));
        }
        // The existing branch/worktree are deliberately left untouched, exactly as ContinueWithFeedbackAsync/
        // TriggerCiRepairAsync do — the next attempt keeps working on the same changes, so a later publish updates
        // the same pull request.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.task SET status='Pending',claimed_by=NULL,claimed_at=NULL,lease_until=NULL,failure_reason=NULL,failed_at=NULL,completed_at=NULL WHERE id=@taskId",
            new { taskId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor) VALUES(@taskId,'Published','Pending',@reason,'orchestrator')",
            new { taskId, reason = $"{newlyIngested.Count} new reviewer comment(s) ingested" }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return newlyIngested.Count;
    }

    public async Task<OutcomeMetrics> GetOutcomeMetricsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        // Every count is windowed by when the thing itself happened (occurred_at/started_at/synced_at/
        // review_recorded_at), never by the task's own created_at — so a long-lived task's older history never
        // leaks into a later window, and a task created before the window can still contribute events within it.
        const string sql = """
            WITH events AS (SELECT * FROM factory.task_event WHERE occurred_at >= @since)
            SELECT
              (SELECT count(*)::int FROM events WHERE to_status='ReadyForPublish') AS "ValidatedReadyForReview",
              (SELECT count(*)::int FROM events WHERE to_status='Completed') AS "MergedAccepted",
              (SELECT count(*)::int FROM events WHERE to_status='Rejected') AS "Rejected",
              (SELECT count(*)::int FROM events WHERE to_status='Pending' AND from_status IS NOT NULL AND from_status <> 'WaitingForQuota') AS "Retries",
              (SELECT count(*)::int FROM events WHERE to_status='WaitingForQuota') AS "QuotaWaitingEvents",
              (SELECT count(*)::int FROM events WHERE to_status='NeedsHuman') AS "HumanInterventions",
              (SELECT count(*)::int FROM factory.agent_run WHERE started_at >= @since AND counts_as_implementation_attempt AND status='Succeeded') AS "AgentProcessSuccesses",
              (SELECT count(*)::int FROM factory.agent_run WHERE started_at >= @since AND counts_as_implementation_attempt AND status='Failed') AS "AgentProcessFailures",
              (SELECT count(*)::int FROM factory.task_ci_status WHERE synced_at >= @since AND overall_status='Success') AS "CiSuccesses",
              (SELECT count(*)::int FROM factory.task_ci_status WHERE synced_at >= @since AND overall_status='Failure') AS "CiFailures",
              (SELECT avg(review_minutes)::float FROM factory.task WHERE review_minutes IS NOT NULL AND review_recorded_at >= @since) AS "AverageReviewMinutes",
              (SELECT count(*)::int FROM factory.task WHERE review_minutes IS NOT NULL AND review_recorded_at >= @since) AS "ReviewedTaskCount"
            """;
        await using var c = Connection();
        var row = await c.QuerySingleAsync<OutcomeMetricsRow>(new CommandDefinition(sql, new { since }, cancellationToken: cancellationToken));
        return row.ToModel(since);
    }

    public async Task SetReviewMinutesAsync(Guid taskId, int minutes, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.task SET review_minutes=@minutes, review_recorded_at=@recordedAt WHERE id=@taskId",
            new { taskId, minutes, recordedAt = clock.UtcNow }, cancellationToken: cancellationToken));
    }

    public async Task<int> CountAgentRunsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        // SF-613: an explicit human continuation resets the budget — only runs since the most recent recorded
        // feedback count, so the fresh allowance it grants is bounded (MaxImplementationAttempts again), never
        // unlimited. With no feedback ever recorded, this counts the task's whole history, unchanged from before.
        const string sql = """
            SELECT count(*)::int FROM factory.agent_run
            WHERE task_id=@taskId AND counts_as_implementation_attempt
              AND started_at > COALESCE((SELECT max(created_at) FROM factory.task_feedback WHERE task_id=@taskId), '-infinity'::timestamptz)
            """;
        await using var c = Connection();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { taskId }, cancellationToken: cancellationToken));
    }

    public async Task<int> CountQuotaInterruptionsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM factory.agent_run WHERE task_id=@taskId AND NOT counts_as_implementation_attempt AND quota_detected", new { taskId }, cancellationToken: cancellationToken));
    }

    public async Task<PreviousAttemptSummary?> GetPreviousAttemptAsync(Guid taskId, CancellationToken cancellationToken)
    {
        // Skips quota-interrupted rows: the agent's context should always reflect the last real implementation
        // attempt, never a content-free quota blip that happens to be more recent.
        const string sql = """
            SELECT ar.result_summary AS "AgentSummary", ar.files_changed::text AS "FilesChangedJson",
              r.files_changed AS "RunFilesChanged", COALESCE(r.lines_added,0) AS "LinesAdded", COALESCE(r.lines_removed,0) AS "LinesRemoved", ar.run_id AS "RunId"
            FROM factory.agent_run ar
            JOIN factory.run r ON r.id = ar.run_id
            WHERE ar.task_id=@taskId AND ar.counts_as_implementation_attempt
            ORDER BY ar.started_at DESC
            LIMIT 1
            """;
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<PreviousAttemptRow>(new CommandDefinition(sql, new { taskId }, cancellationToken: cancellationToken));
        if (row is null) return null;

        var failedSteps = (await c.QueryAsync<FailedValidationStepRow>(new CommandDefinition(
            "SELECT step_type AS \"StepType\", COALESCE(error,'') AS \"Error\", output AS \"Output\" FROM factory.step WHERE run_id=@runId AND step_type IN ('Build','Test') AND status='Failed' ORDER BY started_at",
            new { runId = row.RunId }, cancellationToken: cancellationToken))).AsList();
        var validationOutput = failedSteps.Count == 0 ? null : string.Join("\n\n", failedSteps.Select(s => $"{s.StepType} failed: {s.Error}\n{s.Output}".Trim()));

        var changedFiles = row.RunFilesChanged ?? (row.FilesChangedJson is null ? [] : JsonSerializer.Deserialize<string[]>(row.FilesChangedJson) ?? []);
        return new PreviousAttemptSummary(row.AgentSummary, validationOutput, changedFiles, row.LinesAdded, row.LinesRemoved);
    }

    public async Task<int> ResumeExpiredQuotaTasksAsync(IReadOnlyList<string> configuredAgents, CancellationToken cancellationToken)
    {
        // Resumption is scheduled from provider availability (factory.agent_availability), never a waiting
        // task's own invocation history: a task that never got invoked at all (every configured agent was
        // already at quota on its first attempt) has no history to key off, and a task that last used a
        // now-still-blocked provider must still resume the moment any other configured provider frees up.
        //
        // Only the single highest-priority candidate is resumed per call (LIMIT 1), deliberately mirroring
        // ClaimNextAsync's own one-at-a-time claiming: bulk-flipping every waiting task to Pending the instant
        // one provider frees up would let every task behind the first redo repository preparation and worktree
        // setup for nothing the moment that provider's allowance runs out again, before they even get a real
        // attempt. Worker calls this once per poll-loop iteration, so genuine capacity still drains the queue
        // promptly; it just never resumes further than what current availability can actually justify.
        const string sql = """
            WITH available AS (
              SELECT EXISTS (
                SELECT 1 FROM unnest(@configuredAgents) AS agent(name)
                WHERE NOT COALESCE(
                  (SELECT detected AND reset_at > now() FROM factory.agent_availability aa WHERE aa.agent = agent.name), false)
                  AND NOT COALESCE((SELECT paused FROM factory.dispatch_pause dp WHERE dp.scope = agent.name), false)
              ) AS any_available
            ), candidate AS (
              SELECT t.id FROM factory.task t, available
              WHERE t.status='WaitingForQuota' AND NOT t.repair_paused AND available.any_available
              ORDER BY t.priority DESC, t.created_at
              FOR UPDATE OF t SKIP LOCKED LIMIT 1
            ), updated AS (
              UPDATE factory.task SET status='Pending', claimed_by=NULL, claimed_at=NULL, lease_until=NULL
              WHERE id IN (SELECT id FROM candidate)
              RETURNING id
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT id,'WaitingForQuota','Pending','A configured provider became available; resumed automatically.','orchestrator' FROM updated
            )
            SELECT count(*)::int FROM updated
            """;
        await using var c = Connection();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { configuredAgents }, cancellationToken: cancellationToken));
    }

    public async Task<bool> IsAgentAtQuotaAsync(string agent, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COALESCE(detected AND reset_at > now(), false)
            FROM factory.agent_availability
            WHERE agent=@agent
            """;
        await using var c = Connection();
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { agent }, cancellationToken: cancellationToken));
    }

    public async Task RecordAgentQuotaStatusAsync(AgentQuotaStatus status, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.agent_availability(agent,detected,quota_window,reset_kind,reset_at,checked_at,detail)
            VALUES(@Agent,@Detected,@Window,@ResetKind,@ResetAt,@CheckedAt,@Detail)
            ON CONFLICT(agent) DO UPDATE SET
              detected=excluded.detected, quota_window=excluded.quota_window, reset_kind=excluded.reset_kind,
              reset_at=excluded.reset_at, checked_at=excluded.checked_at, detail=excluded.detail
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new
        {
            status.Agent, status.Detected, Window = status.Window.ToString(), ResetKind = status.ResetKind.ToString(),
            status.ResetAt, status.CheckedAt, status.Detail
        }, cancellationToken: cancellationToken));
    }

    public async Task<AgentQuotaStatus?> GetAgentQuotaStatusAsync(string agent, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT agent AS "Agent", detected AS "Detected", quota_window AS "Window", reset_kind AS "ResetKind",
              reset_at AS "ResetAt", checked_at AS "CheckedAt", detail AS "Detail"
            FROM factory.agent_availability WHERE agent=@agent
            """;
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<AgentQuotaStatusRow>(new CommandDefinition(sql, new { agent }, cancellationToken: cancellationToken));
        return row?.ToModel();
    }

    public async Task<bool> ClearAgentQuotaAsync(string agent, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.agent_availability
            SET detected=false, quota_window='None', reset_kind='None', reset_at=NULL, checked_at=now()
            WHERE agent=@agent
            """;
        await using var c = Connection();
        var rows = await c.ExecuteAsync(new CommandDefinition(sql, new { agent }, cancellationToken: cancellationToken));
        return rows > 0;
    }

    public Task<bool> IsDispatchPausedAsync(CancellationToken cancellationToken) => IsPausedAsync(DispatchPauseScope.Global, cancellationToken);
    public Task<bool> IsAgentPausedAsync(string agent, CancellationToken cancellationToken) => IsPausedAsync(agent, cancellationToken);

    private async Task<bool> IsPausedAsync(string scope, CancellationToken cancellationToken)
    {
        const string sql = "SELECT paused FROM factory.dispatch_pause WHERE scope=@scope";
        await using var c = Connection();
        return await c.ExecuteScalarAsync<bool?>(new CommandDefinition(sql, new { scope }, cancellationToken: cancellationToken)) ?? false;
    }

    public async Task<DispatchPauseState> GetDispatchPauseAsync(string scope, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT scope AS "Scope",paused AS "Paused",reason AS "Reason",paused_at AS "PausedAt",paused_by AS "PausedBy"
            FROM factory.dispatch_pause WHERE scope=@scope
            """;
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<DispatchPauseRow>(new CommandDefinition(sql, new { scope }, cancellationToken: cancellationToken));
        return row?.ToModel() ?? DispatchPauseState.NotPaused(scope);
    }

    public async Task<IReadOnlyList<DispatchPauseState>> GetAllDispatchPausesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT scope AS "Scope",paused AS "Paused",reason AS "Reason",paused_at AS "PausedAt",paused_by AS "PausedBy"
            FROM factory.dispatch_pause
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<DispatchPauseRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SetDispatchPauseAsync(string scope, bool paused, string? reason, string actor, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.dispatch_pause(scope,paused,reason,paused_at,paused_by)
            VALUES(@scope,@paused,@reason,CASE WHEN @paused THEN now() ELSE NULL END,CASE WHEN @paused THEN @actor ELSE NULL END)
            ON CONFLICT(scope) DO UPDATE SET
              paused=excluded.paused, reason=excluded.reason, paused_at=excluded.paused_at, paused_by=excluded.paused_by
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new { scope, paused, reason, actor }, cancellationToken: cancellationToken));
    }

    public async Task SetPriorityAsync(Guid taskId, int priority, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET priority=@priority WHERE id=@taskId", new { taskId, priority }, cancellationToken: cancellationToken));
    }

    public async Task<AddDependencyOutcome> AddDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        return await InsertDependencyEdgeAsync(c, taskId, dependsOnTaskId, source: null, cancellationToken);
    }

    public async Task<AddDependencyOutcome> AddTrackerBatchDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        return await InsertDependencyEdgeAsync(c, taskId, dependsOnTaskId, "tracker-batch", cancellationToken);
    }

    /// <summary>Shared by the manual SF-611 dashboard path (<see cref="AddDependencyAsync"/>, <paramref name="source"/>
    /// <see langword="null"/>) and SF-710's issue-body reconciliation (<paramref name="source"/> <c>"issue"</c>).</summary>
    private static async Task<AddDependencyOutcome> InsertDependencyEdgeAsync(NpgsqlConnection c, Guid taskId, Guid dependsOnTaskId, string? source, CancellationToken cancellationToken)
    {
        if (taskId == dependsOnTaskId) return AddDependencyOutcome.SelfDependency;

        var existing = (await c.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM factory.task WHERE id = ANY(@ids)", new { ids = new[] { taskId, dependsOnTaskId } }, cancellationToken: cancellationToken))).ToHashSet();
        if (!existing.Contains(taskId) || !existing.Contains(dependsOnTaskId)) return AddDependencyOutcome.TaskNotFound;

        // A cycle would close if dependsOnTaskId can already (transitively) reach taskId through existing edges —
        // i.e. dependsOnTaskId already depends, directly or indirectly, on taskId — checked before inserting,
        // not just against the direct edge, since a longer chain is just as real a cycle as a direct one.
        const string cycleSql = """
            WITH RECURSIVE reachable(id) AS (
              SELECT depends_on_task_id FROM factory.task_dependency WHERE task_id=@dependsOnTaskId
              UNION
              SELECT td.depends_on_task_id FROM factory.task_dependency td JOIN reachable r ON td.task_id=r.id
            )
            SELECT EXISTS(SELECT 1 FROM reachable WHERE id=@taskId)
            """;
        var wouldCycle = await c.ExecuteScalarAsync<bool>(new CommandDefinition(cycleSql, new { taskId, dependsOnTaskId }, cancellationToken: cancellationToken));
        if (wouldCycle) return AddDependencyOutcome.WouldCreateCycle;

        const string insertSql = "INSERT INTO factory.task_dependency(task_id,depends_on_task_id,source) VALUES(@taskId,@dependsOnTaskId,@source) ON CONFLICT DO NOTHING RETURNING task_id";
        var inserted = await c.ExecuteScalarAsync<Guid?>(new CommandDefinition(insertSql, new { taskId, dependsOnTaskId, source }, cancellationToken: cancellationToken));
        return inserted is null ? AddDependencyOutcome.AlreadyExists : AddDependencyOutcome.Added;
    }

    public async Task<Guid?> FindTaskIdForIssueAsync(string owner, string name, int issueNumber, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.id FROM github.repository r
            JOIN github.issue i ON i.repository_id = r.id AND i.issue_number = @issueNumber
            JOIN factory.task t ON t.github_issue_id = i.id
            WHERE lower(r.owner) = lower(@owner) AND lower(r.name) = lower(@name)
            """;
        await using var c = Connection();
        return await c.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql, new { owner, name, issueNumber }, cancellationToken: cancellationToken));
    }

    public Task<IssueDependencyReconciliation> ReconcileIssueDependenciesAsync(Guid taskId, IReadOnlyList<Guid> parsedDependsOnTaskIds, CancellationToken cancellationToken) =>
        ReconcileSourcedDependenciesAsync(taskId, "issue", parsedDependsOnTaskIds, cancellationToken);

    public Task<IssueDependencyReconciliation> ReconcileTrackerDependenciesAsync(Guid taskId, IReadOnlyList<Guid> parsedDependsOnTaskIds, CancellationToken cancellationToken) =>
        ReconcileSourcedDependenciesAsync(taskId, "tracker", parsedDependsOnTaskIds, cancellationToken);

    /// <summary>Shared by <see cref="ReconcileIssueDependenciesAsync"/> (SF-710) and <see cref="ReconcileTrackerDependenciesAsync"/>
    /// (SF-707): diffs <paramref name="parsedDependsOnTaskIds"/> against <paramref name="taskId"/>'s existing
    /// edges tagged with this exact <paramref name="source"/>, adding what is missing and removing what is no
    /// longer present — an edge tagged with any other source (a manual one, or the other automatic source) is
    /// never read or touched by either call.</summary>
    private async Task<IssueDependencyReconciliation> ReconcileSourcedDependenciesAsync(Guid taskId, string source, IReadOnlyList<Guid> parsedDependsOnTaskIds, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var parsed = parsedDependsOnTaskIds.Where(id => id != taskId).ToHashSet();
        var existing = (await c.QueryAsync<Guid>(new CommandDefinition(
            "SELECT depends_on_task_id FROM factory.task_dependency WHERE task_id=@taskId AND source=@source", new { taskId, source }, cancellationToken: cancellationToken))).ToHashSet();

        var toRemove = existing.Except(parsed).ToList();
        foreach (var dependsOnTaskId in toRemove)
            await c.ExecuteAsync(new CommandDefinition(
                "DELETE FROM factory.task_dependency WHERE task_id=@taskId AND depends_on_task_id=@dependsOnTaskId AND source=@source",
                new { taskId, dependsOnTaskId, source }, cancellationToken: cancellationToken));

        var added = new List<Guid>();
        var skippedCycles = new List<Guid>();
        foreach (var dependsOnTaskId in parsed.Except(existing))
        {
            var outcome = await InsertDependencyEdgeAsync(c, taskId, dependsOnTaskId, source, cancellationToken);
            if (outcome == AddDependencyOutcome.Added) added.Add(dependsOnTaskId);
            else if (outcome == AddDependencyOutcome.WouldCreateCycle) skippedCycles.Add(dependsOnTaskId);
        }
        return new IssueDependencyReconciliation(added, toRemove, skippedCycles);
    }

    public async Task<bool> CreateForTrackerItemIfEligibleAsync(long repositoryId, string baseBranch, string trackerItemId, string title, string description, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.task(id,repository_id,tracker_item_id,title,description,task_type,status,base_branch,tracker_writeback_section)
            VALUES(@id,@repositoryId,@trackerItemId,@title,@description,'TrackerFile','Pending',@baseBranch,@section)
            ON CONFLICT DO NOTHING;
            """;
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition(sql,
            new { id = Guid.NewGuid(), repositoryId, trackerItemId, title, description, baseBranch, section = TrackerSection.NextUp.ToString() },
            cancellationToken: cancellationToken)) == 1;
    }

    public async Task<Guid?> FindTaskIdForTrackerItemAsync(long repositoryId, string trackerItemId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        return await c.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM factory.task WHERE repository_id=@repositoryId AND tracker_item_id=@trackerItemId",
            new { repositoryId, trackerItemId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TrackerFileTask>> GetTrackerFileTasksAsync(long repositoryId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id AS "TaskId", tracker_item_id AS "TrackerItemId", status AS "Status",
              failure_reason AS "FailureReason", tracker_writeback_section AS "WritebackSection"
            FROM factory.task WHERE repository_id=@repositoryId AND task_type='TrackerFile'
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<TrackerFileTaskRow>(new CommandDefinition(sql, new { repositoryId }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task SetTrackerWritebackSectionAsync(Guid taskId, TrackerSection section, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET tracker_writeback_section=@section WHERE id=@taskId",
            new { taskId, section = section.ToString() }, cancellationToken: cancellationToken));
    }

    public async Task RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM factory.task_dependency WHERE task_id=@taskId AND depends_on_task_id=@dependsOnTaskId", new { taskId, dependsOnTaskId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TaskDependency>> GetDependenciesAsync(Guid taskId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT td.task_id AS "TaskId",td.depends_on_task_id AS "DependsOnTaskId",dep.title AS "DependsOnTitle",dep.status AS "DependsOnStatus",td.source AS "Source"
            FROM factory.task_dependency td JOIN factory.task dep ON dep.id=td.depends_on_task_id
            WHERE td.task_id=@taskId
            ORDER BY td.created_at
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<TaskDependencyRow>(new CommandDefinition(sql, new { taskId }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<int> BlockDependentsOnFailedPrerequisitesAsync(CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Pending, FactoryTaskStatus.NeedsHuman);
        // A prerequisite that ends at Rejected/Cancelled/Failed will never merge, so its dependent must never be
        // silently left queued forever behind it, nor silently released to run against a base that will never
        // actually contain the prerequisite's changes — it moves to NeedsHuman with an explicit reason instead,
        // for the operator to retry the prerequisite, remove the dependency, or cancel the dependent.
        const string sql = """
            WITH blocked AS (
              SELECT DISTINCT ON (td.task_id) td.task_id, dep.title AS blocking_title, dep.status AS blocking_status
              FROM factory.task_dependency td
              JOIN factory.task dep ON dep.id = td.depends_on_task_id
              JOIN factory.task t ON t.id = td.task_id
              WHERE t.status = 'Pending' AND dep.status IN ('Rejected','Cancelled','Failed')
              ORDER BY td.task_id, td.created_at
            ), updated AS (
              UPDATE factory.task t SET status='NeedsHuman',
                failure_reason='Blocked: prerequisite "' || b.blocking_title || '" ended at ' || b.blocking_status || ' without merging.'
              FROM blocked b WHERE t.id = b.task_id
              RETURNING t.id, t.failure_reason
            ), logged AS (
              INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
              SELECT id,'Pending','NeedsHuman',failure_reason,'orchestrator' FROM updated
            )
            SELECT count(*)::int FROM updated
            """;
        await using var c = Connection();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<int> CountOutstandingReviewWorkAsync(CancellationToken cancellationToken)
    {
        await using var c = Connection();
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM factory.task WHERE status IN ('ReadyForPublish','Published')", cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<DigestFinishedTask>> GetRecentlyFinishedTasksAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        // Windowed by task_event.occurred_at (when the transition actually happened), matching GetOutcomeMetricsAsync's
        // own convention, not by task.completed_at/failed_at — Rejected sets neither. t.status=te.to_status excludes a
        // stale event for a task later retried past it (e.g. a Rejected task an operator continued and later
        // completed), so nothing already superseded is ever reported as finished work again.
        const string sql = """
            SELECT t.id AS "TaskId", t.title AS "Title", (gr.owner || '/' || gr.name) AS "Repository",
              i.issue_number AS "IssueNumber", p.pull_request_url AS "PullRequestUrl",
              (te.to_status='Completed') AS "Merged", te.occurred_at AS "FinishedAt"
            FROM factory.task_event te
            JOIN factory.task t ON t.id=te.task_id AND t.status=te.to_status
            JOIN github.repository gr ON gr.id=t.repository_id
            LEFT JOIN github.issue i ON i.id=t.github_issue_id
            LEFT JOIN LATERAL (
              SELECT pull_request_url FROM factory.publication
              WHERE task_id=t.id AND pull_request_url IS NOT NULL ORDER BY completed_at DESC LIMIT 1
            ) p ON true
            WHERE te.to_status IN ('Completed','Rejected') AND te.occurred_at >= @since
            ORDER BY te.occurred_at DESC
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<DigestFinishedTaskRow>(new CommandDefinition(sql, new { since }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<DigestAlertCandidate>> GetOpenCiFailureAlertsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 'Ci' AS "Kind", ('ci:' || t.id) AS "Key", t.title AS "Title",
              COALESCE(cs.error, 'CI checks failing on commit ' || COALESCE(cs.head_commit, 'unknown')) AS "Detail",
              t.id AS "TaskId", p.pull_request_url AS "Url", cs.synced_at AS "UpdatedAt"
            FROM factory.task t
            JOIN factory.task_ci_status cs ON cs.task_id=t.id
            LEFT JOIN LATERAL (
              SELECT pull_request_url FROM factory.publication
              WHERE task_id=t.id AND pull_request_url IS NOT NULL ORDER BY completed_at DESC LIMIT 1
            ) p ON true
            WHERE t.status='Published' AND cs.overall_status='Failure'
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<DigestAlertCandidateRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<DigestAlertCandidate>> GetNeedsHumanAlertsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 'Human' AS "Kind", ('human:' || t.id) AS "Key", t.title AS "Title",
              COALESCE(t.failure_reason, 'Needs human attention') AS "Detail", t.id AS "TaskId", NULL::text AS "Url",
              COALESCE((SELECT max(te.occurred_at) FROM factory.task_event te WHERE te.task_id=t.id AND te.to_status='NeedsHuman'), t.created_at) AS "UpdatedAt"
            FROM factory.task t
            WHERE t.status='NeedsHuman'
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<DigestAlertCandidateRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<DigestAlertCandidate>> GetActiveBlockerAlertsAsync(CancellationToken cancellationToken)
    {
        const string quotaSql = """
            SELECT agent AS "Agent", detected AS "Detected", quota_window AS "Window", reset_kind AS "ResetKind",
              reset_at AS "ResetAt", checked_at AS "CheckedAt", detail AS "Detail"
            FROM factory.agent_availability WHERE detected AND reset_at > now()
            """;
        await using var c = Connection();
        var quotaRows = await c.QueryAsync<AgentQuotaStatusRow>(new CommandDefinition(quotaSql, cancellationToken: cancellationToken));
        var pauses = await GetAllDispatchPausesAsync(cancellationToken);

        var alerts = new List<DigestAlertCandidate>();
        foreach (var quota in quotaRows.Select(r => r.ToModel()))
        {
            var detail = quota.Detail is { Length: > 0 } ? $"Quota blocked: {quota.Detail}" : "Quota blocked";
            if (quota.ResetAt is not null) detail += $" (resets {quota.ResetAt:u})";
            alerts.Add(new DigestAlertCandidate("Quota", $"quota:{quota.Agent}", $"{quota.Agent} at quota", detail, null, null, quota.CheckedAt));
        }
        foreach (var pause in pauses.Where(p => p.Paused))
        {
            var title = pause.Scope == DispatchPauseScope.Global ? "Factory dispatch paused" : $"Agent {pause.Scope} paused";
            var detail = pause.Reason is { Length: > 0 } ? $"Paused by {pause.PausedBy}: {pause.Reason}" : $"Paused by {pause.PausedBy}";
            alerts.Add(new DigestAlertCandidate("Pause", $"pause:{pause.Scope}", title, detail, null, null, pause.PausedAt ?? clock.UtcNow));
        }
        return alerts;
    }
}

/// <summary>Persists digest generations and alert-dedup state (SF-705). Fingerprint comparison itself is
/// <see cref="DigestBuilder"/>'s job (pure, no database access); this store only reads back what was fingerprinted
/// last time and persists what should be remembered next time.</summary>
public sealed class PostgresDigestStore(IOptions<FactoryOptions> options, IClock clock) : IDigestStore
{
    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    public async Task<DigestRun?> GetLatestAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id AS "Id", generated_at AS "GeneratedAt", payload_json AS "PayloadJson",
              delivered AS "Delivered", delivery_target AS "DeliveryTarget", delivery_error AS "DeliveryError"
            FROM factory.digest_run ORDER BY generated_at DESC LIMIT 1
            """;
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<DigestRunRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<DigestRun>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id AS "Id", generated_at AS "GeneratedAt", payload_json AS "PayloadJson",
              delivered AS "Delivered", delivery_target AS "DeliveryTarget", delivery_error AS "DeliveryError"
            FROM factory.digest_run ORDER BY generated_at DESC LIMIT @limit
            """;
        await using var c = Connection();
        var rows = await c.QueryAsync<DigestRunRow>(new CommandDefinition(sql, new { limit }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAlertFingerprintsAsync(CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var rows = await c.QueryAsync<(string alert_key, string fingerprint)>(new CommandDefinition(
            "SELECT alert_key, fingerprint FROM factory.digest_alert_state", cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.alert_key, r => r.fingerprint);
    }

    public async Task<DigestRun> SaveAsync(DigestPayload payload, IReadOnlyList<DigestAlertCandidate> openAlerts, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var generatedAt = clock.UtcNow;
        var payloadJson = JsonSerializer.Serialize(payload);

        await using var connection = Connection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.digest_run(id,generated_at,window_since,window_until,finished_count,ci_failure_count,needs_human_count,blocker_count,payload_json)
            VALUES(@id,@generatedAt,@windowSince,@windowUntil,@finishedCount,@ciFailureCount,@needsHumanCount,@blockerCount,@payloadJson::jsonb)
            """, new
        {
            id, generatedAt, windowSince = payload.WindowSince, windowUntil = payload.WindowUntil,
            finishedCount = payload.FinishedWork.Count, ciFailureCount = payload.CiFailureTotal,
            needsHumanCount = payload.NeedsHumanTotal, blockerCount = payload.BlockerTotal, payloadJson
        }, transaction, cancellationToken: cancellationToken));

        foreach (var alert in openAlerts)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO factory.digest_alert_state(alert_key,kind,fingerprint,first_seen_at,last_seen_at)
                VALUES(@key,@kind,@fingerprint,@now,@now)
                ON CONFLICT(alert_key) DO UPDATE SET fingerprint=excluded.fingerprint, last_seen_at=excluded.last_seen_at
                """, new { key = alert.Key, kind = alert.Kind, fingerprint = DigestBuilder.Fingerprint(alert), now = generatedAt }, transaction, cancellationToken: cancellationToken));
        }

        var openKeys = openAlerts.Select(a => a.Key).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM factory.digest_alert_state WHERE NOT (alert_key = ANY(@openKeys))",
            new { openKeys }, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return new DigestRun(id, generatedAt, payload, false, null, null);
    }

    public async Task RecordDeliveryAsync(Guid digestId, string target, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.digest_run SET delivered=@succeeded, delivery_target=@target, delivery_error=@error WHERE id=@digestId",
            new { digestId, target, succeeded, error }, cancellationToken: cancellationToken));
    }
}

internal sealed class DigestRunRow
{
    public Guid Id { get; init; }
    public DateTime GeneratedAt { get; init; }
    public string PayloadJson { get; init; } = "";
    public bool Delivered { get; init; }
    public string? DeliveryTarget { get; init; }
    public string? DeliveryError { get; init; }

    public DigestRun ToModel() => new(Id, new DateTimeOffset(DateTime.SpecifyKind(GeneratedAt, DateTimeKind.Utc)),
        JsonSerializer.Deserialize<DigestPayload>(PayloadJson)!, Delivered, DeliveryTarget, DeliveryError);
}

internal sealed class DigestFinishedTaskRow
{
    public Guid TaskId { get; init; }
    public string Title { get; init; } = "";
    public string Repository { get; init; } = "";
    public int? IssueNumber { get; init; }
    public string? PullRequestUrl { get; init; }
    public bool Merged { get; init; }
    public DateTime FinishedAt { get; init; }

    public DigestFinishedTask ToModel() => new(TaskId, Title, Repository, IssueNumber, PullRequestUrl, Merged,
        new DateTimeOffset(DateTime.SpecifyKind(FinishedAt, DateTimeKind.Utc)));
}

internal sealed class DigestAlertCandidateRow
{
    public string Kind { get; init; } = "";
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public Guid? TaskId { get; init; }
    public string? Url { get; init; }
    public DateTime UpdatedAt { get; init; }

    public DigestAlertCandidate ToModel() => new(Kind, Key, Title, Detail, TaskId, Url,
        new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
}

internal sealed class OutcomeMetricsRow
{
    public int ValidatedReadyForReview { get; init; }
    public int MergedAccepted { get; init; }
    public int Rejected { get; init; }
    public int Retries { get; init; }
    public int QuotaWaitingEvents { get; init; }
    public int HumanInterventions { get; init; }
    public int AgentProcessSuccesses { get; init; }
    public int AgentProcessFailures { get; init; }
    public int CiSuccesses { get; init; }
    public int CiFailures { get; init; }
    public double? AverageReviewMinutes { get; init; }
    public int ReviewedTaskCount { get; init; }

    public OutcomeMetrics ToModel(DateTimeOffset since) => new(since, ValidatedReadyForReview, MergedAccepted, Rejected, Retries,
        QuotaWaitingEvents, HumanInterventions, AgentProcessSuccesses, AgentProcessFailures, CiSuccesses, CiFailures, AverageReviewMinutes, ReviewedTaskCount);
}

internal sealed class TaskCiStatusRow
{
    public Guid TaskId { get; init; }
    public string OverallStatus { get; init; } = "";
    public string? HeadCommit { get; init; }
    public string ChecksJson { get; init; } = "[]";
    public string? Error { get; init; }
    public DateTime SyncedAt { get; init; }
    public string? RepairTriggeredForCommit { get; init; }

    public TaskCiStatus ToModel() => new(TaskId, OverallStatus, HeadCommit,
        JsonSerializer.Deserialize<List<PullRequestCheck>>(ChecksJson) ?? [], Error, new DateTimeOffset(DateTime.SpecifyKind(SyncedAt, DateTimeKind.Utc)), RepairTriggeredForCommit);
}

internal sealed class TaskMergeStatusRow
{
    public Guid TaskId { get; init; }
    public string Status { get; init; } = "";
    public string? HeadSha { get; init; }
    public string? BaseSha { get; init; }
    public string? Mergeable { get; init; }
    public string? MergeStateStatus { get; init; }
    public string? Error { get; init; }
    public DateTime SyncedAt { get; init; }
    public TaskMergeStatus ToModel() => new(TaskId, Status, HeadSha, BaseSha, Mergeable, MergeStateStatus,
        Error, new DateTimeOffset(DateTime.SpecifyKind(SyncedAt, DateTimeKind.Utc)));
}

internal sealed class TaskRepairPauseRow
{
    public string Status { get; init; } = "";
    public bool RepairPaused { get; init; }
}

internal sealed class ManualMergeCandidateRow
{
    public string Status { get; init; } = "";
    public string? FailureReason { get; init; }
    public string? BranchName { get; init; }
    public string? ValidatedHeadCommit { get; init; }
    public string RepositoryOwner { get; init; } = "";
    public string RepositoryName { get; init; } = "";
    public int? PullRequestNumber { get; init; }
}

internal sealed class ManualMergeOutcomeRow
{
    public Guid TaskId { get; init; }
    public string Status { get; init; } = "";
}

internal sealed class TaskFeedbackRow
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public string Body { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string CreatedBy { get; init; } = "";

    public TaskFeedback ToModel() => new(Id, TaskId, Body, new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)), CreatedBy);
}

internal sealed class ReviewFindingRow
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public Guid RunId { get; init; }
    public string Agent { get; init; } = "";
    public string Severity { get; init; } = "";
    public string? File { get; init; }
    public int? Line { get; init; }
    public string Description { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    public PersistedReviewFinding ToModel() => new(Id, TaskId, RunId, Agent, Severity, File, Line, Description,
        new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
}

internal sealed class TaskDependencyRow
{
    public Guid TaskId { get; init; }
    public Guid DependsOnTaskId { get; init; }
    public string DependsOnTitle { get; init; } = "";
    public string DependsOnStatus { get; init; } = "";
    public string? Source { get; init; }

    public TaskDependency ToModel() => new(TaskId, DependsOnTaskId, DependsOnTitle, Enum.Parse<FactoryTaskStatus>(DependsOnStatus), Source);
}

internal sealed class DispatchPauseRow
{
    public string Scope { get; init; } = "";
    public bool Paused { get; init; }
    public string? Reason { get; init; }
    public DateTime? PausedAt { get; init; }
    public string? PausedBy { get; init; }

    public DispatchPauseState ToModel() => new(Scope, Paused, Reason,
        PausedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(PausedAt.Value, DateTimeKind.Utc)), PausedBy);
}

internal sealed class AgentQuotaStatusRow
{
    public string Agent { get; init; } = "";
    public bool Detected { get; init; }
    public string Window { get; init; } = "";
    public string ResetKind { get; init; } = "";
    public DateTime? ResetAt { get; init; }
    public DateTime CheckedAt { get; init; }
    public string? Detail { get; init; }

    public AgentQuotaStatus ToModel() => new(Agent, Detected, Enum.Parse<QuotaWindow>(Window), Enum.Parse<QuotaResetKind>(ResetKind),
        ResetAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(ResetAt.Value, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(CheckedAt, DateTimeKind.Utc)), Detail);
}

internal sealed class PreviousAttemptRow
{
    public string? AgentSummary { get; init; }
    public string? FilesChangedJson { get; init; }
    public string[]? RunFilesChanged { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public Guid RunId { get; init; }
}

internal sealed class FailedValidationStepRow
{
    public string StepType { get; init; } = "";
    public string Error { get; init; } = "";
    public string? Output { get; init; }
}

internal sealed class PublicationClaimRow
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
}

internal sealed class PublicationDetailsRow
{
    public string? BranchName { get; init; }
    public string? WorktreePath { get; init; }
    public string BaseBranch { get; init; } = "";
    public string? ValidatedHeadCommit { get; init; }
    public long RepositoryId { get; init; }
    public string RepositoryOwner { get; init; } = "";
    public string RepositoryName { get; init; } = "";
    public string TaskTitle { get; init; } = "";
    public int? IssueNumber { get; init; }
    public bool RequireHumanMerge { get; init; }
}
