using Dapper;
using Factory.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Npgsql;

public sealed record AgentRunUsageView(bool IsKnown, long? InputTokens, long? CachedInputTokens, long? OutputTokens,
    long? ReasoningTokens, long? CacheWriteInputTokens, bool? InputTokensIncludesCachedInput, long? TotalInputTokens,
    double? CachedInputShare, string? Source)
{
    public string TokenUnit => "tokens";
}

public static class AgentRunUsageMapper
{
    public static AgentRunUsageView Map(long? inputTokens, long? cachedInputTokens, long? outputTokens,
        long? reasoningTokens, long? cacheWriteInputTokens, bool? inputIncludesCached, string? source)
    {
        long? totalInput = null;
        if (inputTokens is { } input && inputIncludesCached == true) totalInput = input;
        else if (inputTokens is { } uncached && inputIncludesCached == false
            && cachedInputTokens is { } cached && cacheWriteInputTokens is { } written)
        {
            try { totalInput = checked(uncached + cached + written); }
            catch (OverflowException) { }
        }

        double? cacheShare = cachedInputTokens is { } cachedCount && totalInput is > 0 && cachedCount <= totalInput.Value
            ? (double)cachedCount / totalInput.Value
            : null;
        return new(source is not null, inputTokens, cachedInputTokens, outputTokens, reasoningTokens,
            cacheWriteInputTokens, inputIncludesCached, totalInput, cacheShare, source);
    }
}

public sealed class AgentRunUsageRow
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public Guid RunId { get; init; }
    public string Agent { get; init; } = "";
    public string? Provider { get; init; }
    public string Purpose { get; init; } = "Implement";
    public string? Model { get; init; }
    public string? TaskClass { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationSeconds { get; init; }
    public string Status { get; init; } = "";
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public long? CacheWriteInputTokens { get; init; }
    public bool? InputTokensIncludesCachedInput { get; init; }
    public string? UsageSource { get; init; }

    public AgentRunUsageResponse ToResponse() => new(Id, TaskId, RunId, Provider ?? LegacyProvider(Agent), Agent,
        Model, Purpose, TaskClass, StartedAt, CompletedAt, DurationSeconds, Status,
        AgentRunUsageMapper.Map(InputTokens, CachedInputTokens, OutputTokens, ReasoningTokens,
            CacheWriteInputTokens, InputTokensIncludesCachedInput, UsageSource));

    private static string LegacyProvider(string agent) => agent is "Codex-Luna" or "Codex-Sol" ? "Codex" : agent;
}

public sealed record AgentRunUsageResponse(Guid Id, Guid TaskId, Guid RunId, string Provider, string Agent,
    string? Model, string Purpose, string? TaskClass, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    double? DurationSeconds, string Outcome, AgentRunUsageView Usage);

public sealed record AgentUsageAggregate(string Provider, string? Model, string Purpose, string? TaskClass,
    int RunCount, int KnownUsageRunCount, int UnknownUsageRunCount,
    long? TotalInputTokens, int InputUsageRunCount, long? TotalCachedInputTokens, int CachedInputRunCount,
    double? CachedInputShare, int CachedInputShareRunCount, long? TotalOutputTokens, int OutputUsageRunCount,
    long? TotalReasoningTokens, int ReasoningUsageRunCount, long? TotalCacheWriteInputTokens, int CacheWriteUsageRunCount,
    double? TotalAgentWallTimeSeconds, double? AverageDurationSeconds, int SuccessfulRunCount, int FailedRunCount,
    int OtherOutcomeRunCount)
{
    public string TokenUnit => "tokens";
}

public static class AgentRunUsageEndpoints
{
    private const string TotalInputExpression = """
        CASE
          WHEN input_tokens IS NULL THEN NULL
          WHEN input_tokens_includes_cached_input IS TRUE THEN input_tokens
          WHEN input_tokens_includes_cached_input IS FALSE
            AND cached_input_tokens IS NOT NULL AND cache_write_input_tokens IS NOT NULL
            THEN input_tokens + cached_input_tokens + cache_write_input_tokens
          ELSE NULL
        END
        """;

    public static IEndpointRouteBuilder MapAgentRunUsageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agent-runs/{id:guid}", async (Guid id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = await db.OpenConnectionAsync(ct);
            var row = await c.QuerySingleOrDefaultAsync<AgentRunUsageRow>(new CommandDefinition("""
                SELECT id,task_id AS "TaskId",run_id AS "RunId",agent,provider,purpose,model,
                  task_class AS "TaskClass",started_at AS "StartedAt",completed_at AS "CompletedAt",
                  duration_seconds AS "DurationSeconds",status,input_tokens AS "InputTokens",
                  cached_input_tokens AS "CachedInputTokens",output_tokens AS "OutputTokens",
                  reasoning_tokens AS "ReasoningTokens",cache_write_input_tokens AS "CacheWriteInputTokens",
                  input_tokens_includes_cached_input AS "InputTokensIncludesCachedInput",usage_source AS "UsageSource"
                FROM factory.agent_run WHERE id=@id
                """, new { id }, cancellationToken: ct));
            return row is null ? Results.NotFound() : Results.Ok(row.ToResponse());
        });

        endpoints.MapGet("/api/metrics/agent-usage", async (NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var c = await db.OpenConnectionAsync(ct);
            var sql = $"""
                SELECT COALESCE(provider, CASE WHEN agent IN ('Codex-Luna','Codex-Sol') THEN 'Codex' ELSE agent END) AS "Provider",
                  model AS "Model",purpose AS "Purpose",task_class AS "TaskClass",count(*)::int AS "RunCount",
                  count(*) FILTER(WHERE usage_source IS NOT NULL)::int AS "KnownUsageRunCount",
                  count(*) FILTER(WHERE usage_source IS NULL)::int AS "UnknownUsageRunCount",
                  sum(({TotalInputExpression}))::bigint AS "TotalInputTokens",
                  count(*) FILTER(WHERE ({TotalInputExpression}) IS NOT NULL)::int AS "InputUsageRunCount",
                  sum(cached_input_tokens)::bigint AS "TotalCachedInputTokens",
                  count(cached_input_tokens)::int AS "CachedInputRunCount",
                  (sum(cached_input_tokens) FILTER(WHERE cached_input_tokens IS NOT NULL AND ({TotalInputExpression}) IS NOT NULL AND cached_input_tokens <= ({TotalInputExpression}))::numeric
                    / NULLIF(sum(({TotalInputExpression})) FILTER(WHERE cached_input_tokens IS NOT NULL AND ({TotalInputExpression}) IS NOT NULL AND cached_input_tokens <= ({TotalInputExpression})),0))::double precision AS "CachedInputShare",
                  count(*) FILTER(WHERE cached_input_tokens IS NOT NULL AND ({TotalInputExpression}) IS NOT NULL AND cached_input_tokens <= ({TotalInputExpression}))::int AS "CachedInputShareRunCount",
                  sum(output_tokens)::bigint AS "TotalOutputTokens",count(output_tokens)::int AS "OutputUsageRunCount",
                  sum(reasoning_tokens)::bigint AS "TotalReasoningTokens",count(reasoning_tokens)::int AS "ReasoningUsageRunCount",
                  sum(cache_write_input_tokens)::bigint AS "TotalCacheWriteInputTokens",count(cache_write_input_tokens)::int AS "CacheWriteUsageRunCount",
                  sum(duration_seconds)::double precision AS "TotalAgentWallTimeSeconds",
                  avg(duration_seconds)::double precision AS "AverageDurationSeconds",
                  count(*) FILTER(WHERE status='Succeeded')::int AS "SuccessfulRunCount",
                  count(*) FILTER(WHERE status='Failed')::int AS "FailedRunCount",
                  count(*) FILTER(WHERE status NOT IN ('Succeeded','Failed'))::int AS "OtherOutcomeRunCount"
                FROM factory.agent_run
                GROUP BY 1,2,3,4
                ORDER BY 1,2 NULLS LAST,3,4 NULLS LAST
                """;
            var items = await c.QueryAsync<AgentUsageAggregate>(new CommandDefinition(sql, cancellationToken: ct));
            return Results.Ok(items);
        });

        return endpoints;
    }
}
