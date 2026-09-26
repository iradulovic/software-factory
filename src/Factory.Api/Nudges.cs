using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Factory.Api;

public sealed record NudgeCandidate(string Key, string Fingerprint, string Kind, string Title,
    string Explanation, string Href, Guid? TaskId);

public sealed class NudgeRow
{
    public Guid Id { get; init; }
    public string AttentionKey { get; init; } = "";
    public int Generation { get; init; }
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string Href { get; init; } = "";
    public Guid? TaskId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public DateTimeOffset? ReadAt { get; init; }
    public string DeliveryStatus { get; init; } = "";
    public int DeliveryAttempts { get; init; }
    public string? DeliveryError { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
}

public static class NudgePolicy
{
    // These states require action or explain why sequential work cannot advance. Stale observations and
    // individual provider quota reports are deliberately excluded; they are not confirmed blockers.
    private static readonly HashSet<string> ActionableKinds =
    ["NeedsHuman", "RepairsStopped", "RepeatedAttempts", "RepeatedCiRepair", "MergeConflict",
     "CiFailure", "ReadyToMerge", "AutomaticMergeRejected", "DispatchPaused", "ReviewBacklog",
     "AllAgentsUnavailable", "Worker", "RepositorySync"];

    public static IReadOnlyList<NudgeCandidate> Select(IEnumerable<AttentionItem> items) => items
        .Where(item => ActionableKinds.Contains(item.Kind))
        .Select(item =>
        {
            // Attention text can include agent output, repository errors, or user supplied issue text. Only
            // these fixed descriptions leave the machine. The raw reason contributes solely to a hash.
            var (title, explanation) = Describe(item.Kind);
            var material = $"{item.Kind}\n{item.Reason}\n{item.PullRequestNumber}\n{item.Action}";
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
            return new NudgeCandidate(item.Id, fingerprint, item.Kind, title, explanation, item.Href, item.TaskId);
        }).ToList();

    private static (string Title, string Explanation) Describe(string kind) => kind switch
    {
        "NeedsHuman" => ("Task needs a decision", "The task is waiting for operator input; open it to decide the next step."),
        "RepairsStopped" => ("Repairs stopped", "Automatic repair is paused; review the task before resuming."),
        "RepeatedAttempts" => ("Implementation retried", "Repeated attempts may exhaust the task budget; inspect the latest run."),
        "RepeatedCiRepair" => ("CI repair retried", "The pull request has needed another CI repair; inspect the checks."),
        "MergeConflict" => ("Pull request conflict", "GitHub confirmed a conflict that prevents merging."),
        "CiFailure" => ("Pull request CI failed", "Required checks are failing; inspect the pull request."),
        "ReadyToMerge" => ("Human review pull request ready", "Checks and mergeability are confirmed; review and merge when ready."),
        "AutomaticMergeRejected" => ("Automatic merge failed", "The merge request needs operator attention."),
        "DispatchPaused" => ("Dispatch paused", "New tasks cannot be claimed until dispatch resumes."),
        "ReviewBacklog" => ("Review backlog limit reached", "Review or merge outstanding work to allow another task."),
        "AllAgentsUnavailable" => ("No agent can claim work", "All configured agents are unavailable or quota blocked; re-authenticate any agent whose status reports an authentication failure."),
        "Worker" => ("Worker unavailable", "The orchestrator heartbeat is stale; new work may not start."),
        "RepositorySync" => ("Repository sync failed", "Fresh issue and repository state may be unavailable."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed class NudgeStore(NpgsqlDataSource db)
{
    private const string Columns = """
        id AS "Id",attention_key AS "AttentionKey",generation AS "Generation",kind AS "Kind",
        title AS "Title",explanation AS "Explanation",href AS "Href",task_id AS "TaskId",
        occurred_at AS "OccurredAt",resolved_at AS "ResolvedAt",read_at AS "ReadAt",
        delivery_status AS "DeliveryStatus",delivery_attempts AS "DeliveryAttempts",
        delivery_error AS "DeliveryError",delivered_at AS "DeliveredAt"
        """;
    public async Task ReconcileAsync(IReadOnlyList<NudgeCandidate> candidates, DateTimeOffset now, bool webhookEnabled, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await using var transaction = await c.BeginTransactionAsync(ct);
        // Serializes reconciliation across API instances and survives a process restart.
        await c.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(122034)", transaction: transaction, cancellationToken: ct));
        var prior = (await c.QueryAsync<NudgeStateRow>(new CommandDefinition(
            "SELECT attention_key AS \"AttentionKey\",fingerprint,generation,active FROM factory.nudge_state",
            transaction: transaction, cancellationToken: ct))).ToDictionary(row => row.AttentionKey);
        var active = candidates.ToDictionary(candidate => candidate.Key);
        foreach (var candidate in candidates)
        {
            if (prior.TryGetValue(candidate.Key, out var state) && state.Active && state.Fingerprint == candidate.Fingerprint)
                continue;
            var generation = state?.Generation + 1 ?? 1;
            if (state is { Active: true })
                await c.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE factory.nudge SET resolved_at=@now,
                      delivery_status=CASE WHEN delivery_status IN ('Pending','Failed','Sending') THEN 'Superseded' ELSE delivery_status END,
                      next_attempt_at=NULL WHERE attention_key=@key AND resolved_at IS NULL
                    """,
                    new { now, key = candidate.Key }, transaction, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO factory.nudge_state(attention_key,fingerprint,generation,active,changed_at)
                VALUES(@key,@fingerprint,@generation,true,@now)
                ON CONFLICT(attention_key) DO UPDATE SET fingerprint=excluded.fingerprint,generation=excluded.generation,
                  active=true,changed_at=excluded.changed_at
                """, new { candidate.Key, candidate.Fingerprint, generation, now, key = candidate.Key }, transaction, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO factory.nudge(id,attention_key,generation,kind,title,explanation,href,task_id,occurred_at,delivery_status,next_attempt_at)
                VALUES(@id,@key,@generation,@kind,@title,@explanation,@href,@taskId,@now,@status,@nextAttemptAt)
                """, new { id = Guid.NewGuid(), key = candidate.Key, generation, candidate.Kind, candidate.Title,
                    candidate.Explanation, candidate.Href, candidate.TaskId, now,
                    status = webhookEnabled ? "Pending" : "Local", nextAttemptAt = webhookEnabled ? now : (DateTimeOffset?)null },
                transaction, cancellationToken: ct));
        }
        foreach (var state in prior.Values.Where(state => state.Active && !active.ContainsKey(state.AttentionKey)))
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE factory.nudge_state SET active=false,changed_at=@now WHERE attention_key=@key;
                UPDATE factory.nudge SET resolved_at=@now,
                  delivery_status=CASE WHEN delivery_status IN ('Pending','Failed','Sending') THEN 'Superseded' ELSE delivery_status END,
                  next_attempt_at=NULL WHERE attention_key=@key AND resolved_at IS NULL
                """, new { now, key = state.AttentionKey }, transaction, cancellationToken: ct));
        }
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<NudgeRow>> GetRecentAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<NudgeRow>(new CommandDefinition(
            $"SELECT {Columns} FROM factory.nudge ORDER BY occurred_at DESC,id DESC LIMIT @limit", new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM factory.nudge WHERE read_at IS NULL", cancellationToken: ct));
    }

    public async Task<bool> MarkReadAsync(Guid id, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "UPDATE factory.nudge SET read_at=COALESCE(read_at,now()) WHERE id=@id", new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<NudgeRow?> ClaimDeliveryAsync(DateTimeOffset now, CancellationToken ct, string? attentionKey = null)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        var id = await c.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            WITH due AS (
              SELECT id FROM factory.nudge WHERE delivery_status IN ('Pending','Failed','Sending') AND resolved_at IS NULL
                AND (@attentionKey IS NULL OR attention_key=@attentionKey)
                AND next_attempt_at<=@now
                AND (SELECT count(*) FROM factory.nudge WHERE delivered_at>@minuteAgo
                  OR (delivery_status='Failed' AND next_attempt_at>@now AND next_attempt_at<=@minuteAhead)
                  OR (delivery_status='Sending' AND next_attempt_at>@now)) < 5
              ORDER BY occurred_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE factory.nudge n SET delivery_status='Sending',delivery_attempts=n.delivery_attempts+1,
              next_attempt_at=@leaseUntil,delivery_error=NULL
            FROM due WHERE n.id=due.id RETURNING n.id
            """, new { now, attentionKey, minuteAgo = now.AddMinutes(-1), minuteAhead = now.AddMinutes(1),
                leaseUntil = now.AddMinutes(2) }, cancellationToken: ct));
        return id is null ? null : await c.QuerySingleAsync<NudgeRow>(new CommandDefinition(
            $"SELECT {Columns} FROM factory.nudge WHERE id=@id", new { id }, cancellationToken: ct));
    }

    public async Task RecordDeliveryAsync(Guid id, bool delivered, string? error, DateTimeOffset now, CancellationToken ct)
    {
        await using var c = await db.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.nudge SET delivery_status=@status,delivery_error=@error,delivered_at=@deliveredAt,
              next_attempt_at=@nextAttemptAt WHERE id=@id AND delivery_status='Sending'
            """, new { id, status = delivered ? "Delivered" : "Failed", error,
                deliveredAt = delivered ? now : (DateTimeOffset?)null,
                nextAttemptAt = delivered ? null : (DateTimeOffset?)now.AddMinutes(1) }, cancellationToken: ct));
    }
}

internal sealed class NudgeStateRow
{
    public string AttentionKey { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public int Generation { get; init; }
    public bool Active { get; init; }
}
