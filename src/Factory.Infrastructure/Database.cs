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

    public FactoryTask ToModel() => new(Id, RepositoryId, GitHubIssueId, IssueNumber, Title, Description, TaskType, Priority,
        Enum.Parse<FactoryTaskStatus>(Status), PreferredAgent, BaseBranch, BranchName, WorktreePath, ClaimedBy, Offset(ClaimedAt), Offset(LeaseUntil),
        Offset(CreatedAt), Offset(StartedAt), Offset(CompletedAt), Offset(FailedAt), FailureReason);

    private static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? Offset(DateTime? value) => value is null ? null : Offset(value.Value);
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

    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    public async Task<FactoryTask?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidate AS (
              SELECT id,status AS old_status,status <> 'Pending' AS recovered
              FROM factory.task
              WHERE (status='Pending' AND NOT EXISTS (
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
                OR (status = ANY(@executingStatuses) AND lease_until < now())
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
                started_at=COALESCE(started_at,now()),failure_reason=NULL,current_agent=NULL
              FROM candidate c WHERE t.id=c.id
              RETURNING t.id,
                t.repository_id AS "RepositoryId",
                t.github_issue_id AS "GitHubIssueId",
                t.title,t.description,
                t.task_type AS "TaskType",
                t.priority,t.status,
                t.preferred_agent AS "PreferredAgent",
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
                t.failure_reason AS "FailureReason"
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
              claimed."FailureReason"
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
            WHERE id=@taskId AND claimed_by=@workerId AND lease_until >= now()
            """;
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, new { taskId, workerId, lease }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task ReleaseLeaseAsync(Guid taskId, string workerId, CancellationToken cancellationToken)
    {
        await using var connection = Connection();
        await connection.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET lease_until=now() - interval '1 second' WHERE id=@taskId AND claimed_by=@workerId", new { taskId, workerId }, cancellationToken: cancellationToken));
    }

    public async Task<bool> CreateForIssueIfEligibleAsync(GitHubIssue issue, string baseBranch, CancellationToken cancellationToken)
    {
        if (!issue.Labels.Contains("factory:ready", StringComparer.OrdinalIgnoreCase) || !issue.State.Equals("OPEN", StringComparison.OrdinalIgnoreCase)) return false;
        const string sql = """
            INSERT INTO factory.task(id,repository_id,github_issue_id,title,description,status,base_branch)
            VALUES(@id,@repositoryId,@issueId,@title,@body,'Pending',@baseBranch)
            ON CONFLICT DO NOTHING;
            """;
        await using var connection = Connection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, new { id = Guid.NewGuid(), repositoryId = issue.RepositoryId, issueId = issue.Id, issue.Title, issue.Body, baseBranch }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task TransitionAsync(Guid taskId, FactoryTaskStatus expected, FactoryTaskStatus next, string? failureReason, CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(expected, next);
        var completion = next == FactoryTaskStatus.Completed ? ", completed_at=now()" : next == FactoryTaskStatus.Failed ? ", failed_at=now()" : "";
        // Leaving active execution releases the worker's ownership right here, at the single application-level
        // transition boundary, so a resting task's now-meaningless lease can never make ClaimNextAsync mistake it
        // for an abandoned execution (this is what keeps a validated ReadyForPublish task, for example, from being
        // implemented again while it waits for a human to publish it).
        var releaseOwnership = TaskStateMachine.ExecutingStatuses.Contains(next) ? "" : ", claimed_by=NULL, claimed_at=NULL, lease_until=NULL";
        var sql = $"""
            WITH updated AS (
              UPDATE factory.task SET status=@next, failure_reason=@failureReason{completion}{releaseOwnership}, current_agent=NULL WHERE id=@taskId AND status=@expected
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

    public Task<bool> CancelAsync(Guid taskId, CancellationToken cancellationToken) =>
        TransitionFromCurrentAsync(taskId, [FactoryTaskStatus.Pending, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing,
            FactoryTaskStatus.Planning, FactoryTaskStatus.Validating, FactoryTaskStatus.Reviewing, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published,
            FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed],
            FactoryTaskStatus.Cancelled, false, "Cancelled by operator", cancellationToken);

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
        await using var c = Connection(); await c.ExecuteAsync(new CommandDefinition("UPDATE factory.step SET status=@status,completed_at=@now,duration_ms=CAST(EXTRACT(EPOCH FROM (@now-started_at))*1000 AS BIGINT),error=@error,output=@output WHERE id=@stepId", new { stepId, status = status.ToString(), now = clock.UtcNow, error, output }, cancellationToken: cancellationToken));
    }

    public async Task SaveAgentRunAsync(AgentRunRecord r, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO factory.agent_run(
              id,task_id,run_id,step_id,agent,started_at,completed_at,duration_seconds,exit_code,status,stdout,stderr,
              quota_detected,quota_reset_at,attempt_number,needs_human,counts_as_implementation_attempt,
              result_json,result_summary,tests_run,tests_passed,files_changed,risks,human_reason)
            VALUES(
              @Id,@TaskId,@RunId,@StepId,@Agent,@StartedAt,@CompletedAt,@DurationSeconds,@ExitCode,@Status,@StandardOutput,@StandardError,
              @QuotaDetected,@QuotaResetAt,@AttemptNumber,@NeedsHuman,@CountsAsImplementationAttempt,
              CAST(@ResultJson AS jsonb),@ResultSummary,CAST(@TestsRun AS jsonb),@TestsPassed,
              CAST(@FilesChanged AS jsonb),CAST(@Risks AS jsonb),@HumanReason)
            """;
        var parameters = new
        {
            r.Id, r.TaskId, r.RunId, r.StepId, r.Agent, r.StartedAt, r.CompletedAt, r.DurationSeconds, r.ExitCode, r.Status,
            r.StandardOutput, r.StandardError, r.QuotaDetected, r.QuotaResetAt, r.AttemptNumber, r.NeedsHuman, r.CountsAsImplementationAttempt,
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
        await using var c = Connection(); await c.ExecuteAsync(new CommandDefinition("UPDATE factory.run SET status=@status,completed_at=@now WHERE id=@runId", new { runId, status = status.ToString(), now = clock.UtcNow }, cancellationToken: cancellationToken));
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
        const string sql = """
            INSERT INTO factory.publication(id,task_id,run_id,status,requested_by)
            VALUES(@id,@taskId,@runId,'Requested',@requestedBy)
            ON CONFLICT (task_id) WHERE status IN ('Requested','Publishing') DO NOTHING
            RETURNING id
            """;
        await using var c = Connection();
        return await c.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql, new { id = Guid.NewGuid(), taskId, runId, requestedBy }, cancellationToken: cancellationToken));
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
              SELECT id FROM factory.publication
              WHERE status='Requested' OR (status='Publishing' AND lease_until < now())
              ORDER BY requested_at FOR UPDATE SKIP LOCKED LIMIT 1
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

    public async Task SetCurrentAgentAsync(Guid taskId, string? agentName, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("UPDATE factory.task SET current_agent=@agentName WHERE id=@taskId", new { taskId, agentName }, cancellationToken: cancellationToken));
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
              t.require_human_merge AS "RequireHumanMerge"
            FROM factory.task t
            JOIN github.repository r ON r.id = t.repository_id
            JOIN LATERAL (
              SELECT pull_request_number FROM factory.publication
              WHERE task_id = t.id AND status = 'PullRequestCreated' AND pull_request_number IS NOT NULL
              ORDER BY completed_at DESC LIMIT 1
            ) p ON true
            WHERE t.status = 'Published'
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

    public async Task<TaskCiStatus?> GetCiStatusAsync(Guid taskId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT task_id AS "TaskId", overall_status AS "OverallStatus", head_commit AS "HeadCommit",
              checks_json AS "ChecksJson", error AS "Error", synced_at AS "SyncedAt"
            FROM factory.task_ci_status WHERE task_id=@taskId
            """;
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<TaskCiStatusRow>(new CommandDefinition(sql, new { taskId }, cancellationToken: cancellationToken));
        return row?.ToModel();
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
            "SELECT count(*)::int FROM factory.agent_run WHERE task_id=@taskId AND NOT counts_as_implementation_attempt", new { taskId }, cancellationToken: cancellationToken));
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
              WHERE t.status='WaitingForQuota' AND available.any_available
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
        if (taskId == dependsOnTaskId) return AddDependencyOutcome.SelfDependency;

        await using var c = Connection();
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

        const string insertSql = "INSERT INTO factory.task_dependency(task_id,depends_on_task_id) VALUES(@taskId,@dependsOnTaskId) ON CONFLICT DO NOTHING RETURNING task_id";
        var inserted = await c.ExecuteScalarAsync<Guid?>(new CommandDefinition(insertSql, new { taskId, dependsOnTaskId }, cancellationToken: cancellationToken));
        return inserted is null ? AddDependencyOutcome.AlreadyExists : AddDependencyOutcome.Added;
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
            SELECT td.task_id AS "TaskId",td.depends_on_task_id AS "DependsOnTaskId",dep.title AS "DependsOnTitle",dep.status AS "DependsOnStatus"
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

    public TaskCiStatus ToModel() => new(TaskId, OverallStatus, HeadCommit,
        JsonSerializer.Deserialize<List<PullRequestCheck>>(ChecksJson) ?? [], Error, new DateTimeOffset(DateTime.SpecifyKind(SyncedAt, DateTimeKind.Utc)));
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

internal sealed class TaskDependencyRow
{
    public Guid TaskId { get; init; }
    public Guid DependsOnTaskId { get; init; }
    public string DependsOnTitle { get; init; } = "";
    public string DependsOnStatus { get; init; } = "";

    public TaskDependency ToModel() => new(TaskId, DependsOnTaskId, DependsOnTitle, Enum.Parse<FactoryTaskStatus>(DependsOnStatus));
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
