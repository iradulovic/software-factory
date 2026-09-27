using System.Text.Json;
using Dapper;
using Factory.Core;

namespace Factory.Infrastructure;

public sealed partial class PostgresTaskStore
{
    public async Task PauseForAgentHumanRequestAsync(Guid taskId, Guid runId, Guid agentRunId, AgentHumanRequest request,
        string reason, string? branchName, string? headCommit, CancellationToken cancellationToken)
    {
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.Implementing, FactoryTaskStatus.NeedsHuman);
        await using var c = Connection();
        await c.OpenAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        var status = await c.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM factory.task WHERE id=@taskId FOR UPDATE", new { taskId }, tx, cancellationToken: cancellationToken));
        if (status != "Implementing") throw new InvalidOperationException($"Task {taskId} was not implementing when its agent requested human input.");
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.agent_human_request
              (id,task_id,agent_run_id,kind,prompt,choices,checks,context,branch_name,head_commit)
            VALUES (@id,@taskId,@agentRunId,@kind,@prompt,CAST(@choices AS jsonb),CAST(@checks AS jsonb),@context,@branchName,@headCommit)
            """, new { id = Guid.NewGuid(), taskId, agentRunId, kind = request.Kind, prompt = request.Prompt,
                choices = JsonSerializer.Serialize(request.Choices ?? []), checks = JsonSerializer.Serialize(request.Checks ?? []),
                context = request.Context, branchName, headCommit }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.task SET status='NeedsHuman',failure_reason=@reason,claimed_by=NULL,claimed_at=NULL,
              lease_until=NULL,current_agent=NULL,current_agent_reason=NULL WHERE id=@taskId
            """, new { taskId, reason }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
            VALUES(@taskId,'Implementing','NeedsHuman',@reason,'orchestrator')
            """, new { taskId, reason }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.run SET status='Succeeded',completed_at=now() WHERE id=@runId AND status='Running'
            """, new { runId }, tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
    }

    private const string HumanRequestSql = """
        SELECT hr.id AS "Id",hr.task_id AS "TaskId",hr.agent_run_id AS "AgentRunId",hr.kind AS "Kind",
          hr.prompt AS "Prompt",hr.choices::text AS "ChoicesJson",hr.checks::text AS "ChecksJson",
          hr.context AS "Context",hr.branch_name AS "BranchName",hr.head_commit AS "HeadCommit",
          hr.continuation_head_commit AS "ContinuationHeadCommit",
          hr.created_at AS "CreatedAt",hr.resolution AS "Resolution",hr.answer AS "Answer",hr.resolved_at AS "ResolvedAt",
          ar.result_json::text AS "ResultJson",ar.agent AS "AgentName",ar.purpose AS "AgentPurpose",
          ar.provider_session_id AS "ProviderSessionId"
        FROM factory.agent_human_request hr JOIN factory.agent_run ar ON ar.id=hr.agent_run_id
        """;

    public async Task<IReadOnlyList<PersistedAgentHumanRequest>> GetAgentHumanRequestsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var rows = await c.QueryAsync<AgentHumanRequestRow>(new CommandDefinition(
            HumanRequestSql + " WHERE hr.task_id=@taskId ORDER BY hr.created_at DESC,hr.id DESC", new { taskId }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<PersistedAgentHumanRequest?> GetPostImplementationRequestAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var row = await c.QuerySingleOrDefaultAsync<AgentHumanRequestRow>(new CommandDefinition(
            HumanRequestSql + " WHERE hr.id=(SELECT post_implementation_request_id FROM factory.task WHERE id=@taskId) AND hr.resolution='passed'",
            new { taskId }, cancellationToken: cancellationToken));
        return row?.ToModel();
    }

    public async Task AdvancePostImplementationHeadAsync(Guid taskId, string headCommit, CancellationToken cancellationToken)
    {
        await using var c = Connection();
        var changed = await c.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.agent_human_request hr SET continuation_head_commit=@headCommit
            FROM factory.task t
            WHERE t.id=@taskId AND t.status='Validating' AND t.post_implementation_request_id=hr.id
              AND hr.kind='verification' AND hr.resolution='passed'
            """, new { taskId, headCommit }, cancellationToken: cancellationToken));
        if (changed != 1) throw new InvalidOperationException($"Task {taskId} has no active verified continuation to advance.");
    }

    public async Task<bool> ResolveAgentHumanRequestAsync(Guid taskId, Guid requestId, string resolution, string answer,
        string? branchName, string? headCommit, CancellationToken cancellationToken)
    {
        if (resolution is not ("answer" or "passed" or "failed") || string.IsNullOrWhiteSpace(answer)) return false;
        await using var c = Connection();
        await c.OpenAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        var task = await c.QuerySingleOrDefaultAsync<HumanTaskRow>(new CommandDefinition(
            "SELECT status AS \"Status\",branch_name AS \"BranchName\" FROM factory.task WHERE id=@taskId FOR UPDATE",
            new { taskId }, tx, cancellationToken: cancellationToken));
        if (task?.Status != "NeedsHuman") return false;
        var request = await c.QuerySingleOrDefaultAsync<AgentHumanRequestRow>(new CommandDefinition(
            HumanRequestSql + " WHERE hr.id=@requestId AND hr.task_id=@taskId AND hr.resolution IS NULL FOR UPDATE OF hr",
            new { requestId, taskId }, tx, cancellationToken: cancellationToken));
        if (request is null || (resolution == "answer") != (request.Kind == "decision") ||
            (resolution == "passed" && (request.Kind != "verification" ||
                !string.Equals(request.BranchName, branchName, StringComparison.Ordinal) ||
                !string.Equals(request.HeadCommit, headCommit, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(task.BranchName, branchName, StringComparison.Ordinal)))) return false;
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Pending);
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.agent_human_request SET resolution=@resolution,answer=@answer,resolved_at=now(),resolved_by='operator'
            WHERE id=@requestId
            """, new { resolution, answer, requestId }, tx, cancellationToken: cancellationToken));
        if (resolution != "passed")
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO factory.task_feedback(id,task_id,body,created_by)
                VALUES(@id,@taskId,@feedback,'operator')
                """, new { id = Guid.NewGuid(), taskId, feedback = answer }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.task SET status='Pending',claimed_by=NULL,claimed_at=NULL,lease_until=NULL,
              failure_reason=NULL,failed_at=NULL,completed_at=NULL,post_implementation_request_id=@resumeId
            WHERE id=@taskId
            """, new { taskId, resumeId = resolution == "passed" ? requestId : (Guid?)null }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
            VALUES(@taskId,'NeedsHuman','Pending',@reason,'human')
            """, new { taskId, reason = resolution == "passed" ? "Verification passed; post-implementation gates scheduled" : "Agent request resolved with feedback" }, tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ClassifyLegacyVerificationAsync(Guid taskId, string checks, string branchName,
        string headCommit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checks) || string.IsNullOrWhiteSpace(branchName) ||
            string.IsNullOrWhiteSpace(headCommit) || headCommit.Length != 40 || !headCommit.All(Uri.IsHexDigit)) return false;
        await using var c = Connection();
        await c.OpenAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        var task = await c.QuerySingleOrDefaultAsync<HumanTaskRow>(new CommandDefinition(
            "SELECT status AS \"Status\",branch_name AS \"BranchName\" FROM factory.task WHERE id=@taskId FOR UPDATE",
            new { taskId }, tx, cancellationToken: cancellationToken));
        if (task?.Status != "NeedsHuman" || task.BranchName != branchName) return false;
        TaskStateMachine.EnsureCanTransition(FactoryTaskStatus.NeedsHuman, FactoryTaskStatus.Pending);
        var agent = await c.QuerySingleOrDefaultAsync<LegacyAgentRow>(new CommandDefinition("""
            SELECT id AS "Id",purpose AS "Purpose",result_json->>'status' AS "ResultStatus",needs_human AS "NeedsHuman",
              result_json->>'humanRequest' AS "Structured"
            FROM factory.agent_run WHERE task_id=@taskId AND counts_as_implementation_attempt
            ORDER BY started_at DESC,id DESC LIMIT 1
            """, new { taskId }, tx, cancellationToken: cancellationToken));
        if (agent is null || agent.Purpose != "Implement" || agent.ResultStatus != "completed" || !agent.NeedsHuman || agent.Structured is not null) return false;
        if (await c.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS(SELECT 1 FROM factory.agent_human_request WHERE task_id=@taskId)
              OR EXISTS(
                SELECT 1 FROM factory.task_event e JOIN factory.agent_run ar ON ar.id=@agentRunId
                WHERE e.task_id=@taskId AND e.to_status='Pending' AND e.occurred_at > ar.completed_at)
            """, new { taskId, agentRunId = agent.Id }, tx, cancellationToken: cancellationToken))) return false;
        var id = Guid.NewGuid();
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.agent_human_request
              (id,task_id,agent_run_id,kind,prompt,checks,branch_name,head_commit,resolution,answer,resolved_at,resolved_by)
            VALUES(@id,@taskId,@agentRunId,'verification','Operator-classified legacy verification',CAST(@checks AS jsonb),
              @branchName,@headCommit,'passed',@answer,now(),'operator')
            """, new { id, taskId, agentRunId = agent.Id, checks = JsonSerializer.Serialize(new[] { checks }), branchName,
                headCommit, answer = checks }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE factory.task SET status='Pending',claimed_by=NULL,claimed_at=NULL,lease_until=NULL,
              failure_reason=NULL,failed_at=NULL,completed_at=NULL,post_implementation_request_id=@id WHERE id=@taskId
            """, new { id, taskId }, tx, cancellationToken: cancellationToken));
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO factory.task_event(task_id,from_status,to_status,reason,actor)
            VALUES(@taskId,'NeedsHuman','Pending','Legacy verification passed; post-implementation gates scheduled','human')
            """, new { taskId }, tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
        return true;
    }
}

internal sealed class AgentHumanRequestRow
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public Guid AgentRunId { get; init; }
    public string Kind { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string ChoicesJson { get; init; } = "[]";
    public string ChecksJson { get; init; } = "[]";
    public string? Context { get; init; }
    public string? BranchName { get; init; }
    public string? HeadCommit { get; init; }
    public string? ContinuationHeadCommit { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Resolution { get; init; }
    public string? Answer { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResultJson { get; init; }
    public string? AgentName { get; init; }
    public string? AgentPurpose { get; init; }
    public string? ProviderSessionId { get; init; }
    public PersistedAgentHumanRequest ToModel() => new(Id, TaskId, AgentRunId, Kind, Prompt,
        JsonSerializer.Deserialize<string[]>(ChoicesJson) ?? [], JsonSerializer.Deserialize<string[]>(ChecksJson) ?? [],
        Context, BranchName, HeadCommit, CreatedAt, Resolution, Answer, ResolvedAt,
        ResultJson is null ? null : JsonSerializer.Deserialize<AgentResult>(ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        AgentName, AgentPurpose, ProviderSessionId, ContinuationHeadCommit);
}

internal sealed class HumanTaskRow
{
    public string Status { get; init; } = "";
    public string? BranchName { get; init; }
}

internal sealed class LegacyAgentRow
{
    public Guid Id { get; init; }
    public string Purpose { get; init; } = "";
    public string? ResultStatus { get; init; }
    public bool NeedsHuman { get; init; }
    public string? Structured { get; init; }
}
