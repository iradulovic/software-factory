public sealed record TaskResponse(
    Guid Id,
    string Title,
    string Repository,
    int? IssueNumber,
    string Status,
    int Priority,
    string Agent,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? BranchName,
    string? WorktreePath,
    string? FailureReason,
    int? ReviewMinutes,
    bool RequireHumanMerge,
    string? Result,
    double DurationSeconds,
    string? AgentModel = null,
    string? AgentReasoningEffort = null,
    string? AgentSelectionReason = null,
    string? AgentRoutingError = null,
    string? TaskClass = null);

/// <summary>Dapper binds a no-default-constructor type's (a record's) constructor parameters to the query's
/// columns POSITIONALLY, not by name - <see cref="TaskListSql"/> in Program.cs must list its columns in exactly
/// this order. Dapper also has no built-in numeric widening or <see cref="DateTime"/> -&gt;
/// <see cref="DateTimeOffset"/> conversion for that positional path (Npgsql reads a <c>timestamptz</c> column as
/// <see cref="DateTime"/>, and Postgres's <c>EXTRACT(EPOCH FROM ...)</c> as <see cref="decimal"/>, not
/// <see cref="double"/>): a mismatch on either count throws "no matching constructor" for every row regardless
/// of its actual values, which is exactly how querying directly into <see cref="TaskResponse"/> used to fail.
/// This intermediate shape matches Npgsql's actual column order and types exactly, then <see cref="ToResponse"/>
/// converts to the public <see cref="TaskResponse"/> shape.</summary>
public sealed record TaskRow(
    Guid Id,
    string Title,
    string Repository,
    int? IssueNumber,
    string Status,
    int Priority,
    string Agent,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? BranchName,
    string? WorktreePath,
    string? FailureReason,
    int? ReviewMinutes,
    bool RequireHumanMerge,
    string? Result,
    decimal DurationSeconds,
    string? AgentModel,
    string? AgentReasoningEffort,
    string? AgentSelectionReason,
    string? AgentRoutingError,
    string? TaskClass,
    string BaseBranch = "main")
{
    private static DateTimeOffset Offset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? Offset(DateTime? value) => value is null ? null : Offset(value.Value);

    public TaskResponse ToResponse() => new(Id, Title, Repository, IssueNumber, Status, Priority, Agent,
        Offset(CreatedAt), Offset(StartedAt), Offset(CompletedAt), BranchName, WorktreePath, FailureReason,
        ReviewMinutes, RequireHumanMerge, Result, (double)DurationSeconds, AgentModel, AgentReasoningEffort,
        AgentSelectionReason, AgentRoutingError, TaskClass);
}
