using System.Text.Json;

public sealed class AgentRunDetailsRow
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public string Agent { get; init; } = "";
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
}

public sealed record AgentRunDetails(
    Guid Id, Guid RunId, string Agent, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, double? DurationSeconds,
    int? ExitCode, string Status, string? Stdout, string? Stderr, bool QuotaDetected, int AttemptNumber, bool NeedsHuman,
    JsonElement? ResultJson, string? ResultSummary, IReadOnlyList<string> TestsRun, bool? TestsPassed,
    IReadOnlyList<string> FilesChanged, IReadOnlyList<string> Risks, string? HumanReason);

public static class AgentRunDetailsMapper
{
    public static AgentRunDetails Map(AgentRunDetailsRow row) => new(
        row.Id, row.RunId, row.Agent, row.StartedAt, row.CompletedAt, row.DurationSeconds, row.ExitCode, row.Status,
        row.Stdout, row.Stderr, row.QuotaDetected, row.AttemptNumber, row.NeedsHuman,
        ParseDocument(row.ResultJson), row.ResultSummary, ParseList(row.TestsRunJson), row.TestsPassed,
        ParseList(row.FilesChangedJson), ParseList(row.RisksJson), row.HumanReason);

    private static JsonElement? ParseDocument(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<JsonElement>(json);

    private static IReadOnlyList<string> ParseList(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
}
