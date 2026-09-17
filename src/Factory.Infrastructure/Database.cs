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

public sealed class PostgresTaskStore(IOptions<FactoryOptions> options, IClock clock) : ITaskStore
{
    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    public async Task<FactoryTask?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidate AS (
              SELECT id,status <> 'Pending' AS recovered
              FROM factory.task
              WHERE status='Pending'
                OR (status IN ('Claimed','Preparing','Planning','Implementing','Validating','Reviewing','ReadyForPublish') AND lease_until < now())
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
            )
            UPDATE factory.task t SET status='Claimed',claimed_by=@workerId,claimed_at=now(),lease_until=now()+@lease,
              started_at=COALESCE(started_at,now()),failure_reason=NULL
            FROM candidate c WHERE t.id=c.id
            RETURNING t.id,
              t.repository_id AS "RepositoryId",
              t.github_issue_id AS "GitHubIssueId",
              (SELECT issue_number FROM github.issue WHERE id=t.github_issue_id) AS "IssueNumber",
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
              t.failure_reason AS "FailureReason";
            """;
        await using var connection = Connection();
        var row = await connection.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(sql, new { workerId, lease }, cancellationToken: cancellationToken));
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
        await using var connection = Connection();
        var count = await connection.ExecuteAsync(new CommandDefinition($"UPDATE factory.task SET status=@next, failure_reason=@failureReason {completion} WHERE id=@taskId AND status=@expected", new { taskId, expected = expected.ToString(), next = next.ToString(), failureReason }, cancellationToken: cancellationToken));
        if (count != 1) throw new InvalidOperationException($"Task {taskId} was not in expected state {expected}.");
    }

    public Task<bool> RetryAsync(Guid taskId, CancellationToken cancellationToken) =>
        TransitionFromCurrentAsync(taskId, [FactoryTaskStatus.Failed, FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman], FactoryTaskStatus.Pending, true, cancellationToken);

    public Task<bool> CancelAsync(Guid taskId, CancellationToken cancellationToken) =>
        TransitionFromCurrentAsync(taskId, [FactoryTaskStatus.Pending, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing,
            FactoryTaskStatus.Planning, FactoryTaskStatus.Validating, FactoryTaskStatus.Reviewing, FactoryTaskStatus.ReadyForPublish,
            FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed],
            FactoryTaskStatus.Cancelled, false, cancellationToken);

    private async Task<bool> TransitionFromCurrentAsync(Guid taskId, IReadOnlyCollection<FactoryTaskStatus> allowedSources, FactoryTaskStatus next, bool resetExecution, CancellationToken cancellationToken)
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
        var id = Guid.NewGuid(); await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO factory.step(id,run_id,step_type,status,started_at,attempt) VALUES(@id,@runId,@stepType,'Running',@now,@attempt)", new { id, runId, stepType, now = clock.UtcNow, attempt }, cancellationToken: cancellationToken)); return id;
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
              quota_detected,quota_reset_at,attempt_number,needs_human,result_json,result_summary,tests_run,tests_passed,files_changed,risks,human_reason)
            VALUES(
              @Id,@TaskId,@RunId,@StepId,@Agent,@StartedAt,@CompletedAt,@DurationSeconds,@ExitCode,@Status,@StandardOutput,@StandardError,
              @QuotaDetected,@QuotaResetAt,@AttemptNumber,@NeedsHuman,CAST(@ResultJson AS jsonb),@ResultSummary,CAST(@TestsRun AS jsonb),@TestsPassed,
              CAST(@FilesChanged AS jsonb),CAST(@Risks AS jsonb),@HumanReason)
            """;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var parameters = new
        {
            r.Id, r.TaskId, r.RunId, r.StepId, r.Agent, r.StartedAt, r.CompletedAt, r.DurationSeconds, r.ExitCode, r.Status,
            r.StandardOutput, r.StandardError, r.QuotaDetected, r.QuotaResetAt, r.AttemptNumber, r.NeedsHuman,
            ResultJson = r.Result is null ? null : JsonSerializer.Serialize(r.Result, jsonOptions),
            ResultSummary = r.Result?.Summary,
            TestsRun = r.Result is null ? null : JsonSerializer.Serialize(r.Result.TestsRun, jsonOptions),
            TestsPassed = r.Result?.TestsPassed,
            FilesChanged = r.Result is null ? null : JsonSerializer.Serialize(r.Result.FilesChanged, jsonOptions),
            Risks = r.Result is null ? null : JsonSerializer.Serialize(r.Result.Risks, jsonOptions),
            HumanReason = r.Result?.HumanReason
        };
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    public async Task CompleteRunAsync(Guid runId, ExecutionStatus status, CancellationToken cancellationToken)
    {
        await using var c = Connection(); await c.ExecuteAsync(new CommandDefinition("UPDATE factory.run SET status=@status,completed_at=@now WHERE id=@runId", new { runId, status = status.ToString(), now = clock.UtcNow }, cancellationToken: cancellationToken));
    }
}
