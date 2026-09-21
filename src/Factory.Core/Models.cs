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

/// <param name="LastSyncedAt">The point in time through which this repository's issues are known to be fully
/// synchronized, used as the incremental sync checkpoint; <see langword="null"/> before the first sync.</param>
public sealed record GitHubRepository(long Id, string Owner, string Name, string CloneUrl, string DefaultBranch, bool IsEnabled, DateTimeOffset? LastSyncedAt = null);

public sealed record GitHubIssue(
    long Id, long RepositoryId, long GitHubIssueId, int IssueNumber, string Title, string Body,
    string State, string Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Labels, IReadOnlyList<GitHubComment> Comments, DateTimeOffset? ClosedAt = null);

public sealed record GitHubComment(long GitHubCommentId, string Author, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record FactoryRun(Guid Id, Guid TaskId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, ExecutionStatus Status, string WorkerId);

public sealed record FactoryStep(Guid Id, Guid RunId, string StepType, ExecutionStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, long? DurationMs, int Attempt, string? Error, string? Output);

public sealed record AgentRunRecord(Guid Id, Guid TaskId, Guid RunId, Guid StepId, string Agent, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, double? DurationSeconds, int? ExitCode, string Status, string? StandardOutput,
    string? StandardError, bool QuotaDetected, DateTimeOffset? QuotaResetAt, int AttemptNumber, bool NeedsHuman, AgentResult? Result);

/// <param name="LogPath">When set, stdout and stderr are streamed to this file as the process runs, interleaved
/// in arrival order, in addition to the bounded preview <see cref="ProcessResult"/> always returns.</param>
public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    string? StandardInput = null,
    string? LogPath = null);

public sealed record ProcessResult(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Cancelled)
{
    public TimeSpan Duration => CompletedAt - StartedAt;
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

public sealed record AgentRunRequest(Guid TaskId, Guid RunId, Guid StepId, string WorkingDirectory, int AttemptNumber, string? LogPath = null);
public sealed record AgentRunResult(ProcessResult Process, AgentResult? Result, string? ValidationError, bool QuotaDetected, DateTimeOffset? QuotaResetAt = null);

public sealed record AgentAvailability(string Agent, bool Available, string? Version, string? Error);

public sealed record AgentResult(string Status, string Summary, IReadOnlyList<string> TestsRun, bool TestsPassed,
    IReadOnlyList<string> FilesChanged, IReadOnlyList<string> Risks, bool NeedsHuman, string? HumanReason);

public sealed record ValidationCommand(string Name, string Executable, IReadOnlyList<string> Arguments);

/// <summary>Where a step's full process output is streamed while it runs, deterministic from IDs the caller
/// already has so no extra round trip is needed to know where to write or read it.</summary>
public static class StepLogPaths
{
    public static string Resolve(string logsDirectory, Guid runId, Guid stepId) =>
        Path.Combine(logsDirectory, runId.ToString(), $"{stepId}.log");
}

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

/// <summary>A resting task with a recorded worktree, identified well enough for <see cref="WorktreeCleanupPolicy"/>
/// to decide whether to remove it and for <see cref="IWorktreeManager"/> to remove it.</summary>
public sealed record WorktreeCleanupCandidate(Guid TaskId, FactoryTaskStatus Status, string WorktreePath, string RepositoryOwner, string RepositoryName);

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

/// <summary>
/// A configuration-driven definition of one CLI coding agent. Every agent has the same shape — executable,
/// arguments, how the prompt is delivered, timeout, and how quota exhaustion shows up in its output — so adding
/// one is a configuration change, never a new class. <see cref="PromptDelivery"/> is <c>"stdin"</c> (the prompt is
/// piped to the process) or <c>"argument"</c> (the prompt is appended to <see cref="Arguments"/>).
/// </summary>
public sealed record AgentProfile(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    string PromptDelivery,
    int TimeoutMinutes,
    IReadOnlyList<string> QuotaSignatures,
    IReadOnlyList<string> VersionArguments,
    int AvailabilityTimeoutSeconds,
    int QuotaCooldownHours);
