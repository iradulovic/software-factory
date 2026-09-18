namespace Factory.Core;

public enum FactoryTaskStatus
{
    Pending, Claimed, Preparing, Planning, Implementing, Validating, Reviewing,
    ReadyForPublish, Published, WaitingForQuota, NeedsHuman, Completed, Rejected, Failed, Cancelled
}

public enum ExecutionStatus { Pending, Running, Succeeded, Failed, Cancelled }

public sealed record FactoryTask(
    Guid Id,
    long RepositoryId,
    long? GitHubIssueId,
    int? IssueNumber,
    string Title,
    string Description,
    string TaskType,
    int Priority,
    FactoryTaskStatus Status,
    string? PreferredAgent,
    string BaseBranch,
    string? BranchName,
    string? WorktreePath,
    string? ClaimedBy,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? FailedAt,
    string? FailureReason);

public sealed record GitHubRepository(long Id, string Owner, string Name, string CloneUrl, string DefaultBranch, bool IsEnabled);

public sealed record GitHubIssue(
    long Id, long RepositoryId, long GitHubIssueId, int IssueNumber, string Title, string Body,
    string State, string Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Labels, IReadOnlyList<GitHubComment> Comments);

public sealed record GitHubComment(long GitHubCommentId, string Author, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record FactoryRun(Guid Id, Guid TaskId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, ExecutionStatus Status, string WorkerId);

public sealed record FactoryStep(Guid Id, Guid RunId, string StepType, ExecutionStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, long? DurationMs, int Attempt, string? Error, string? Output);

public sealed record AgentRunRecord(Guid Id, Guid TaskId, Guid RunId, Guid StepId, string Agent, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, double? DurationSeconds, int? ExitCode, string Status, string? StandardOutput,
    string? StandardError, bool QuotaDetected, DateTimeOffset? QuotaResetAt, int AttemptNumber, bool NeedsHuman, AgentResult? Result);

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    string? StandardInput = null);

public sealed record ProcessResult(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Cancelled)
{
    public TimeSpan Duration => CompletedAt - StartedAt;
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

public sealed record AgentRunRequest(Guid TaskId, Guid RunId, Guid StepId, string WorkingDirectory, int AttemptNumber);
public sealed record AgentRunResult(ProcessResult Process, AgentResult? Result, string? ValidationError, bool QuotaDetected, DateTimeOffset? QuotaResetAt = null);

public sealed record AgentAvailability(string Agent, bool Available, string? Version, string? Error);

public sealed record AgentResult(string Status, string Summary, IReadOnlyList<string> TestsRun, bool TestsPassed,
    IReadOnlyList<string> FilesChanged, IReadOnlyList<string> Risks, bool NeedsHuman, string? HumanReason);

public sealed record ValidationCommand(string Name, string Executable, IReadOnlyList<string> Arguments);

/// <summary><see cref="Publish"/> is "manual" (a human explicitly requests publication) or "auto-draft"
/// (the orchestrator requests it itself as soon as a run reaches <c>ReadyForPublish</c>).</summary>
public sealed record RepositoryConfiguration(string BaseBranch, IReadOnlyList<string> BuildCommands, IReadOnlyList<string> TestCommands,
    int MaxImplementationAttempts, int MaxReviewAttempts, bool RequireHumanMerge, string Publish = "manual")
{
    public static RepositoryConfiguration Default { get; } = new("main", ["dotnet build"], ["dotnet test"], 2, 1, true, "manual");
}

/// <summary>Everything <see cref="IGitHubPublisher"/> needs to push a task's committed branch and open a draft pull request for it.</summary>
public sealed record PublicationRequest(Guid Id, Guid TaskId, string BranchName, string WorktreePath, string BaseBranch,
    long RepositoryId, string RepositoryOwner, string RepositoryName, string TaskTitle, int? IssueNumber);

public sealed record PushResult(bool Succeeded, string? Error);
public sealed record PullRequestResult(bool Succeeded, int? Number, string? Url, string? Error);
public sealed record GitHubWriteResult(bool Succeeded, string? Error);
public sealed record PullRequestState(bool Merged, bool Closed);

/// <summary>A task resting in <see cref="FactoryTaskStatus.Published"/>, identified well enough for
/// <see cref="IGitHubClient"/> to look up its pull request's current state.</summary>
public sealed record PublishedTaskRef(Guid TaskId, string RepositoryOwner, string RepositoryName, int PullRequestNumber);

/// <summary>Where a task sits in its bounded implementation-attempt budget, for both enforcement and for
/// telling the agent which attempt this is.</summary>
public sealed record AttemptContext(int Number, int MaxAttempts, PreviousAttemptSummary? Previous);

/// <summary>The prior implementation attempt's outcome, fed back into <c>.factory/task.md</c> so a repeat
/// attempt learns from what went wrong instead of reproducing it.</summary>
public sealed record PreviousAttemptSummary(
    string? AgentSummary,
    string? ValidationOutput,
    IReadOnlyList<string> ChangedFiles,
    int LinesAdded,
    int LinesRemoved);
