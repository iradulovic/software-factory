using System.Text.Json;
using Factory.Core;

public sealed class VerificationWorkspaceRow
{
    public string? BranchName { get; init; }
    public string? WorktreePath { get; init; }
    public string BaseBranch { get; init; } = "main";
}

public sealed class AgentRunDetailsRow
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public string Agent { get; init; } = "";
    public string? Provider { get; init; }
    public string Purpose { get; init; } = "Implement";
    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? SelectionReason { get; init; }
    public string? TaskClass { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public double? DurationSeconds { get; init; }
    public int? ExitCode { get; init; }
    public string Status { get; init; } = "";
    public string? Stdout { get; init; }
    public string? Stderr { get; init; }
    public bool QuotaDetected { get; init; }
    public int AttemptNumber { get; init; }
    public bool NeedsHuman { get; init; }
    public string? ResultJson { get; init; }
    public string? ResultSummary { get; init; }
    public string? TestsRunJson { get; init; }
    public bool? TestsPassed { get; init; }
    public string? FilesChangedJson { get; init; }
    public string? RisksJson { get; init; }
    public string? HumanReason { get; init; }
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public long? CacheWriteInputTokens { get; init; }
    public bool? InputTokensIncludesCachedInput { get; init; }
    public string? UsageSource { get; init; }
}

public sealed record AgentRunDetails(
    Guid Id, Guid RunId, string Agent, string Purpose, string? Model, string? ReasoningEffort, string? SelectionReason,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, double? DurationSeconds,
    int? ExitCode, string Status, string? Stdout, string? Stderr, bool QuotaDetected, int AttemptNumber, bool NeedsHuman,
    JsonElement? ResultJson, string? ResultSummary, IReadOnlyList<string> TestsRun, bool? TestsPassed,
    IReadOnlyList<string> FilesChanged, IReadOnlyList<string> Risks, string? HumanReason, string? TaskClass = null,
    string? Provider = null, AgentRunUsageView? Usage = null);

public static class AgentRunDetailsMapper
{
    public static AgentRunDetails Map(AgentRunDetailsRow row) => new(
        row.Id, row.RunId, row.Agent, row.Purpose, row.Model, row.ReasoningEffort, row.SelectionReason,
        row.StartedAt, row.CompletedAt, row.DurationSeconds, row.ExitCode, row.Status,
        row.Stdout, row.Stderr, row.QuotaDetected, row.AttemptNumber, row.NeedsHuman,
        ParseDocument(row.ResultJson), row.ResultSummary, ParseList(row.TestsRunJson), row.TestsPassed,
        ParseList(row.FilesChangedJson), ParseList(row.RisksJson), row.HumanReason, row.TaskClass, row.Provider,
        AgentRunUsageMapper.Map(row.InputTokens, row.CachedInputTokens, row.OutputTokens, row.ReasoningTokens,
            row.CacheWriteInputTokens, row.InputTokensIncludesCachedInput, row.UsageSource));

    private static JsonElement? ParseDocument(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<JsonElement>(json);

    private static IReadOnlyList<string> ParseList(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
}

public sealed record TaskReviewFindingDetails(Guid Id, Guid TaskId, Guid RunId, string Agent, string Severity,
    string? File, int? Line, string Description, DateTimeOffset CreatedAt, string? MediumImpact, string? Rationale);

public sealed record TaskAgentReviewDetails(Guid Id, Guid TaskId, Guid RunId, string Agent, string Status, string Summary,
    bool NeedsHuman, string? HumanReason, int? Score, string? ScoreRationale, string Disposition, string PolicyReason,
    DateTimeOffset CreatedAt, IReadOnlyList<TaskReviewFindingDetails> Findings);

public static class TaskAgentReviewDetailsMapper
{
    public static TaskAgentReviewDetails Map(PersistedAgentReview review) => new(review.Id, review.TaskId, review.RunId,
        review.Agent, review.Status, review.Summary, review.NeedsHuman, review.HumanReason, review.Score,
        review.ScoreRationale, review.Disposition, review.PolicyReason, review.CreatedAt,
        review.Findings.Select(f => new TaskReviewFindingDetails(f.Id, f.TaskId, f.RunId, f.Agent, f.Severity,
            f.File, f.Line, f.Description, f.CreatedAt, f.MediumImpact, f.Rationale)).ToList());
}
