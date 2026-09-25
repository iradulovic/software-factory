public sealed class CurrentExecutionRow
{
    public Guid TaskId { get; init; }
    public string TaskTitle { get; init; } = "";
    public string TaskStatus { get; init; } = "";
    public DateTimeOffset? TaskStartedAt { get; init; }
    public DateTimeOffset? TaskClaimedAt { get; init; }
    public DateTimeOffset? TaskCreatedAt { get; init; }
    public string RepositoryOwner { get; init; } = "";
    public string RepositoryName { get; init; } = "";
    public int? IssueNumber { get; init; }
    public string? CurrentAgent { get; init; }
    public string? LastAgent { get; init; }
    public Guid? RunId { get; init; }
    public DateTimeOffset? RunStartedAt { get; init; }
    public Guid? StepId { get; init; }
    public string? StepType { get; init; }
    public DateTimeOffset? StepStartedAt { get; init; }
    public int? StepAttempt { get; init; }
    public DateTimeOffset? LastCompletedStepAt { get; init; }
    public int? LastImplementationAttempt { get; init; }
    public int? MaxImplementationAttempts { get; init; }
}

public sealed record CurrentExecutionSnapshot(
    string Status,
    string? TaskStatus,
    Guid? TaskId,
    string? TaskTitle,
    string? TaskUrl,
    string? Repository,
    int? IssueNumber,
    string? IssueUrl,
    string? Agent,
    Guid? RunId,
    DateTimeOffset? RunStartedAt,
    Guid? StepId,
    string? StepType,
    DateTimeOffset? StepStartedAt,
    int? ImplementationAttempt,
    int? MaxImplementationAttempts,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastProgressAt,
    double? ElapsedSeconds,
    double? LastProgressAgeSeconds);

public static class CurrentExecutionProjection
{
    public static CurrentExecutionSnapshot Create(CurrentExecutionRow? row, string dashboardUrl, DateTimeOffset now)
    {
        if (row is null)
            return new CurrentExecutionSnapshot("Idle", null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null, null, null);

        var status = row.TaskStatus == "Stopping" ? "Stopping"
            : row.StepId is not null ? "Running"
            : row.RunId is null ? "Starting"
            : row.LastCompletedStepAt is not null ? "BetweenSteps"
            : "Starting";
        var startedAt = row.StepStartedAt ?? row.RunStartedAt ?? row.TaskClaimedAt ?? row.TaskStartedAt ?? row.TaskCreatedAt;
        var lastProgressAt = row.StepStartedAt ?? row.LastCompletedStepAt ?? row.RunStartedAt ?? row.TaskClaimedAt ?? row.TaskStartedAt ?? row.TaskCreatedAt;
        var taskUrl = $"{dashboardUrl.TrimEnd('/')}/tasks/{row.TaskId}";
        var issueUrl = row.IssueNumber is null ? null
            : $"https://github.com/{Uri.EscapeDataString(row.RepositoryOwner)}/{Uri.EscapeDataString(row.RepositoryName)}/issues/{row.IssueNumber}";
        var agent = row.RunId is null ? null : row.CurrentAgent ?? row.LastAgent;
        if (agent is "Codex-Luna" or "Codex-Sol") agent = "Codex";

        return new CurrentExecutionSnapshot(status, row.TaskStatus, row.TaskId, row.TaskTitle, taskUrl,
            $"{row.RepositoryOwner}/{row.RepositoryName}", row.IssueNumber, issueUrl,
            agent, row.RunId, row.RunStartedAt,
            row.StepId, row.StepType, row.StepStartedAt,
            row.StepType == "AgentImplementation" ? row.StepAttempt : row.LastImplementationAttempt,
            row.MaxImplementationAttempts, startedAt, lastProgressAt,
            AgeSeconds(now, startedAt), AgeSeconds(now, lastProgressAt));
    }

    private static double? AgeSeconds(DateTimeOffset now, DateTimeOffset? timestamp) =>
        timestamp is null ? null : Math.Max(0, (now - timestamp.Value).TotalSeconds);
}

public static class CurrentExecutionQuery
{
    // One candidate task and at most one current run, running step, completed step, implementation attempt,
    // and actual agent are read. The lateral lookups are backed by migration 031's descending partial indexes.
    public const string Sql = """
        WITH candidate AS (
          SELECT t.id,t.title,t.status,t.started_at,t.claimed_at,t.created_at,t.current_agent,
            gr.owner AS repository_owner,gr.name AS repository_name,i.issue_number
          FROM factory.task t
          JOIN github.repository gr ON gr.id=t.repository_id
          LEFT JOIN github.issue i ON i.id=t.github_issue_id
          WHERE t.status IN ('Claimed','Preparing','Planning','Implementing','Validating','Reviewing','Stopping')
          ORDER BY COALESCE(t.claimed_at,t.started_at,t.created_at) DESC,t.id DESC
          LIMIT 1
        )
        SELECT t.id AS "TaskId",t.title AS "TaskTitle",t.status AS "TaskStatus",
          t.started_at AS "TaskStartedAt",t.claimed_at AS "TaskClaimedAt",t.created_at AS "TaskCreatedAt",
          t.repository_owner AS "RepositoryOwner",t.repository_name AS "RepositoryName",t.issue_number AS "IssueNumber",
          t.current_agent AS "CurrentAgent",last_agent.agent AS "LastAgent",
          current_run.id AS "RunId",current_run.started_at AS "RunStartedAt",
          current_step.id AS "StepId",current_step.step_type AS "StepType",current_step.started_at AS "StepStartedAt",
          current_step.attempt AS "StepAttempt",last_completed.completed_at AS "LastCompletedStepAt",
          last_implementation.attempt AS "LastImplementationAttempt",
          NULLIF(current_run.repository_configuration->>'maxImplementationAttempts','')::int AS "MaxImplementationAttempts"
        FROM candidate t
        LEFT JOIN LATERAL (
          SELECT r.id,r.started_at,r.repository_configuration
          FROM factory.run r
          WHERE r.task_id=t.id AND r.status='Running'
          ORDER BY r.started_at DESC,r.id DESC
          LIMIT 1
        ) current_run ON TRUE
        LEFT JOIN LATERAL (
          SELECT s.id,s.step_type,s.started_at,s.attempt
          FROM factory.step s
          WHERE s.run_id=current_run.id AND s.status='Running'
          ORDER BY s.started_at DESC,s.id DESC
          LIMIT 1
        ) current_step ON TRUE
        LEFT JOIN LATERAL (
          SELECT s.completed_at
          FROM factory.step s
          WHERE s.run_id=current_run.id AND s.completed_at IS NOT NULL
          ORDER BY s.completed_at DESC,s.started_at DESC,s.id DESC
          LIMIT 1
        ) last_completed ON TRUE
        LEFT JOIN LATERAL (
          SELECT s.attempt
          FROM factory.step s
          WHERE s.run_id=current_run.id AND s.step_type='AgentImplementation'
          ORDER BY s.started_at DESC,s.id DESC
          LIMIT 1
        ) last_implementation ON TRUE
        LEFT JOIN LATERAL (
          SELECT ar.agent
          FROM factory.agent_run ar
          WHERE ar.run_id=current_run.id
          ORDER BY ar.started_at DESC,ar.id DESC
          LIMIT 1
        ) last_agent ON TRUE
        """;
}
