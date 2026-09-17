public sealed class AgentStatsRow
{
    public string? ActiveTask { get; init; }
    public int RunsToday { get; init; }
    public int SuccessfulRuns { get; init; }
    public DateTimeOffset? QuotaDetectedAt { get; init; }
    public DateTimeOffset? QuotaResetAt { get; init; }
}

public sealed record AgentStatus(
    string Agent, bool Available, string? Version, string? Error, string? ActiveTask,
    int RunsToday, int SuccessfulRuns, DateTimeOffset? QuotaDetectedAt, DateTimeOffset? QuotaResetAt);
