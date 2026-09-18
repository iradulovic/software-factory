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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private NpgsqlConnection Connection() => new(options.Value.ConnectionString);

    public async Task<FactoryTask?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidate AS (
              SELECT id,status AS old_status,status <> 'Pending' AS recovered
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
            ), claimed AS (
              UPDATE factory.task t SET status='Claimed',claimed_by=@workerId,claimed_at=now(),lease_until=now()+@lease,
                started_at=COALESCE(started_at,now()),failure_reason=NULL
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
        var sql = $"""
            WITH updated AS (
              UPDATE factory.task SET status=@next, failure_reason=@failureReason{completion} WHERE id=@taskId AND status=@expected
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
        TransitionFromCurrentAsync(taskId, [FactoryTaskStatus.Failed, FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman], FactoryTaskStatus.Pending, true, "Retried by operator", cancellationToken);

    public Task<bool> CancelAsync(Guid taskId, CancellationToken cancellationToken) =>
        TransitionFromCurrentAsync(taskId, [FactoryTaskStatus.Pending, FactoryTaskStatus.Claimed, FactoryTaskStatus.Preparing, FactoryTaskStatus.Implementing,
            FactoryTaskStatus.Planning, FactoryTaskStatus.Validating, FactoryTaskStatus.Reviewing, FactoryTaskStatus.ReadyForPublish,
            FactoryTaskStatus.WaitingForQuota, FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Failed],
            FactoryTaskStatus.Cancelled, false, "Cancelled by operator", cancellationToken);

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
        var parameters = new
        {
            r.Id, r.TaskId, r.RunId, r.StepId, r.Agent, r.StartedAt, r.CompletedAt, r.DurationSeconds, r.ExitCode, r.Status,
            r.StandardOutput, r.StandardError, r.QuotaDetected, r.QuotaResetAt, r.AttemptNumber, r.NeedsHuman,
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

    public async Task<PublicationRequest?> ClaimNextPublicationAsync(string workerId, CancellationToken cancellationToken)
    {
        const string claimSql = """
            UPDATE factory.publication SET status='Publishing',claimed_by=@workerId,claimed_at=now()
            WHERE id = (SELECT id FROM factory.publication WHERE status='Requested' ORDER BY requested_at FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING id,task_id AS "TaskId"
            """;
        await using var c = Connection();
        var claimed = await c.QuerySingleOrDefaultAsync<PublicationClaimRow>(new CommandDefinition(claimSql, new { workerId }, cancellationToken: cancellationToken));
        if (claimed is null) return null;

        const string detailSql = """
            SELECT t.branch_name AS "BranchName",t.worktree_path AS "WorktreePath",t.base_branch AS "BaseBranch",
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
            details.RepositoryId, details.RepositoryOwner, details.RepositoryName, details.TaskTitle, details.IssueNumber);
    }

    public async Task CompletePublicationAsync(Guid publicationId, string status, int? pullRequestNumber, string? pullRequestUrl, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE factory.publication SET status=@status,pull_request_number=@pullRequestNumber,pull_request_url=@pullRequestUrl,error=@error,completed_at=now()
            WHERE id=@publicationId
            """;
        await using var c = Connection();
        await c.ExecuteAsync(new CommandDefinition(sql, new { publicationId, status, pullRequestNumber, pullRequestUrl, error }, cancellationToken: cancellationToken));
    }
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
    public long RepositoryId { get; init; }
    public string RepositoryOwner { get; init; } = "";
    public string RepositoryName { get; init; } = "";
    public string TaskTitle { get; init; } = "";
    public int? IssueNumber { get; init; }
}
