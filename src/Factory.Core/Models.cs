using System.Text.Json.Serialization;

namespace Factory.Core;

public enum FactoryTaskStatus
{
    Pending, Claimed, Preparing, Planning, Implementing, Validating, Reviewing,
    ReadyForPublish, Published, WaitingForQuota, NeedsHuman, Completed, Rejected, Failed, Cancelled
}

public enum ExecutionStatus { Pending, Running, Succeeded, Failed, Cancelled }

/// <summary>The result of attempting to record a dependency edge (SF-611).</summary>
public enum AddDependencyOutcome { Added, AlreadyExists, WouldCreateCycle, TaskNotFound, SelfDependency }

/// <summary>One prerequisite <paramref name="DependsOnTaskId"/> must reach <see cref="FactoryTaskStatus.Completed"/>
/// before <paramref name="TaskId"/> becomes claimable — <see cref="DependsOnTitle"/>/<see cref="DependsOnStatus"/>
/// are read alongside the edge so the operator can see what is blocking a task without a second lookup.
/// <paramref name="Source"/> is <c>"issue"</c> when SF-710 parsed this edge from the dependent task's GitHub issue
/// body, or <see langword="null"/> when an operator added it manually through the dashboard (SF-611).</summary>
public sealed record TaskDependency(Guid TaskId, Guid DependsOnTaskId, string DependsOnTitle, FactoryTaskStatus DependsOnStatus, string? Source = null);

/// <summary>The result of reconciling a task's <c>source='issue'</c> dependency edges (SF-710) against the current
/// set of task ids parsed from its GitHub issue body on one sync pass. <paramref name="SkippedCycles"/> lists a
/// parsed prerequisite that was not inserted because it would have closed a cycle — never silently dropped.</summary>
public sealed record IssueDependencyReconciliation(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed, IReadOnlyList<Guid> SkippedCycles);

/// <summary>One dependency reference parsed from an issue body's <c>Depends on #N</c> / <c>Blocked by #N</c>
/// convention (SF-710). <see cref="Owner"/>/<see cref="Name"/> are <see langword="null"/> for a same-repository
/// reference (plain <c>#N</c>); both are set for the cross-repository <c>owner/repo#N</c> form, consistent with
/// SF-611's existing cross-repository dependency support.</summary>
public sealed record IssueDependencyRef(string? Owner, string? Name, int IssueNumber);

/// <summary>One piece of operator feedback recorded against a task (SF-613) — a correction or a manual-test
/// failure attached when the operator continues a resting task rather than accepting it as-is. Every feedback
/// row stays permanently, so prior instructions remain auditable even once superseded by a later one.</summary>
public sealed record TaskFeedback(Guid Id, Guid TaskId, string Body, DateTimeOffset CreatedAt, string CreatedBy);

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

/// <param name="CountsAsImplementationAttempt">Whether this invocation counts toward the task's
/// <see cref="RepositoryConfiguration.MaxImplementationAttempts"/> budget. A quota-interrupted invocation never
/// got a real chance to implement anything, so it is recorded here (<c>false</c>) but excluded from that budget
/// by <see cref="ITaskStore.CountAgentRunsAsync"/> — invocation history itself always stays complete.</param>
public sealed record AgentRunRecord(Guid Id, Guid TaskId, Guid RunId, Guid StepId, string Agent, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, double? DurationSeconds, int? ExitCode, string Status, string? StandardOutput,
    string? StandardError, bool QuotaDetected, DateTimeOffset? QuotaResetAt, int AttemptNumber, bool NeedsHuman, AgentResult? Result,
    bool CountsAsImplementationAttempt = true);

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

/// <param name="Window">The classified reset window a detected quota signal falls into; <see cref="QuotaWindow.None"/>
/// when <paramref name="QuotaDetected"/> is <see langword="false"/>. See <see cref="QuotaClassifier"/>.</param>
/// <param name="ResetKind">How confidently <paramref name="QuotaResetAt"/> is known.</param>
/// <param name="QuotaDetail">The short, configured signature string that triggered detection, if any — never the
/// full process output, which is preserved separately.</param>
public sealed record AgentRunResult(ProcessResult Process, AgentResult? Result, string? ValidationError, bool QuotaDetected,
    DateTimeOffset? QuotaResetAt = null, QuotaWindow Window = QuotaWindow.None, QuotaResetKind ResetKind = QuotaResetKind.None, string? QuotaDetail = null);

public sealed record AgentAvailability(string Agent, bool Available, string? Version, string? Error);

/// <summary>An agent's current quota status, persisted independently of any particular task or run — the state
/// <see cref="ITaskStore.IsAgentAtQuotaAsync"/> actually consults. Updated after every invocation of the agent,
/// whether or not quota was detected, so a status that cleared is reflected immediately rather than only by
/// scanning task-run history.</summary>
public sealed record AgentQuotaStatus(string Agent, bool Detected, QuotaWindow Window, QuotaResetKind ResetKind,
    DateTimeOffset? ResetAt, DateTimeOffset CheckedAt, string? Detail);

/// <summary>The reserved <see cref="DispatchPauseState.Scope"/> that pauses the whole factory's new-task
/// dispatch, as distinct from every other scope value, which is a specific agent's own name.</summary>
public static class DispatchPauseScope
{
    public const string Global = "__global__";
}

/// <summary>Durable pause/resume state for one scope (<see cref="DispatchPauseScope.Global"/> or a specific
/// agent's name), operator-initiated and independent of quota (which clears itself) and of cancellation (which
/// stops work already in progress; pause never does — only new dispatch). <paramref name="Reason"/>,
/// <paramref name="PausedAt"/>, and <paramref name="PausedBy"/> are only meaningful while <see cref="Paused"/>.</summary>
public sealed record DispatchPauseState(string Scope, bool Paused, string? Reason, DateTimeOffset? PausedAt, string? PausedBy)
{
    public static DispatchPauseState NotPaused(string scope) => new(scope, false, null, null, null);
}

public sealed record AgentResult(string Status, string Summary, IReadOnlyList<string> TestsRun, bool TestsPassed,
    IReadOnlyList<string> FilesChanged, IReadOnlyList<string> Risks, bool NeedsHuman, string? HumanReason);

/// <summary>The single source of truth for <c>.factory/result.json</c>'s accepted <see cref="AgentResult.Status"/>
/// values (SF-605): both <c>AgentResultReader</c>'s validation and the completion contract generated into
/// <c>.factory/task.md</c> read this same list, so the two can never silently disagree.</summary>
public static class AgentResultContract
{
    public static readonly IReadOnlyList<string> Statuses = ["completed", "failed", "blocked", "needs-human"];
}

/// <summary>An executable and its already-split arguments, never a shell command line. This is the only shape
/// validation steps ever invoke: ".factory/config.json" build/test commands opt into a literal shell explicitly
/// (see <see cref="ValidationCommandJsonConverter"/>), and even then it is just this same shape with the shell
/// itself as the executable — no separate "use a shell" branch exists anywhere downstream.</summary>
[JsonConverter(typeof(ValidationCommandJsonConverter))]
public sealed record ValidationCommand(string Executable, IReadOnlyList<string> Arguments)
{
    public override string ToString() => Arguments.Count == 0 ? Executable : $"{Executable} {string.Join(' ', Arguments)}";

    // A record's synthesized equality compares Arguments (IReadOnlyList<string>) by reference, since lists and
    // arrays don't override Equals themselves; two commands with equal but distinct argument lists must still
    // compare equal (tests, and anything else comparing a parsed command against an expected one, rely on this).
    public bool Equals(ValidationCommand? other) =>
        other is not null && Executable == other.Executable && Arguments.SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Executable);
        foreach (var argument in Arguments) hash.Add(argument);
        return hash.ToHashCode();
    }
}

/// <summary>Where a step's full process output is streamed while it runs, deterministic from IDs the caller
/// already has so no extra round trip is needed to know where to write or read it.</summary>
public static class StepLogPaths
{
    public static string Resolve(string logsDirectory, Guid runId, Guid stepId) =>
        Path.Combine(logsDirectory, runId.ToString(), $"{stepId}.log");
}

/// <param name="Publish">"manual" (a human explicitly requests publication) or "auto-draft" (the orchestrator
/// requests it itself as soon as a run reaches <c>ReadyForPublish</c>).</param>
/// <param name="MaxQuotaInterruptions">A separate bound on repeated quota interruptions (SF-603), so excluding
/// them from <see cref="MaxImplementationAttempts"/> cannot let a task wait on quota forever: once a task has
/// accumulated this many quota-interrupted invocations without a successful implementation attempt, it moves to
/// <c>NeedsHuman</c> instead of waiting again.</param>
public sealed record RepositoryConfiguration(string BaseBranch, IReadOnlyList<ValidationCommand> BuildCommands, IReadOnlyList<ValidationCommand> TestCommands,
    int MaxImplementationAttempts, int MaxReviewAttempts, bool RequireHumanMerge, string Publish = "manual", int MaxQuotaInterruptions = 20)
{
    public static RepositoryConfiguration Default { get; } =
        new("main", [new ValidationCommand("dotnet", ["build"])], [new ValidationCommand("dotnet", ["test"])], 2, 1, true, "manual", 20);
}

/// <summary>Everything <see cref="IGitHubPublisher"/> needs to push a task's committed branch and open a draft pull request for it.</summary>
/// <param name="ValidatedHeadCommit">The head commit the task's implementation was actually validated against, or
/// <see langword="null"/> for a task that reached <see cref="FactoryTaskStatus.ReadyForPublish"/> before this was
/// recorded. When set, publication refuses to push a worktree whose current HEAD no longer matches it.</param>
/// <param name="RequireHumanMerge">This task's effective merge policy (SF-709), computed once by
/// <c>PreparePublicationStep</c>. <see langword="true"/> opens a draft pull request, exactly as before this task;
/// <see langword="false"/> opens it ready for review instead, since no human is expected to look at it before a
/// green CI run merges it automatically.</param>
public sealed record PublicationRequest(Guid Id, Guid TaskId, string BranchName, string WorktreePath, string BaseBranch,
    long RepositoryId, string RepositoryOwner, string RepositoryName, string TaskTitle, int? IssueNumber, string? ValidatedHeadCommit = null,
    bool RequireHumanMerge = true);

public sealed record PushResult(bool Succeeded, string? Error);
public sealed record PullRequestResult(bool Succeeded, int? Number, string? Url, string? Error);
public sealed record GitHubWriteResult(bool Succeeded, string? Error);
public sealed record PullRequestState(bool Merged, bool Closed);

/// <summary>The outcome of requesting <c>gh pr merge</c> (SF-709). A failure (a merge conflict, a protected-branch
/// rejection, insufficient reviews) is reported explicitly via <see cref="Error"/> rather than thrown, so the
/// caller can move the task to <see cref="FactoryTaskStatus.NeedsHuman"/> instead of retrying it forever.</summary>
public sealed record MergeResult(bool Succeeded, string? Error);

/// <summary>A task resting in <see cref="FactoryTaskStatus.Published"/>, identified well enough for
/// <see cref="IGitHubClient"/> to look up its pull request's current state.</summary>
/// <param name="RequireHumanMerge">This task's own effective merge policy (SF-709), decided once by
/// <c>PreparePublicationStep</c> before publication and never re-derived afterward. <see langword="false"/> lets
/// <c>Factory.GitHubSync.Worker</c> request a merge itself once CI on this pull request's head commit is green;
/// <see langword="true"/> leaves the pull request exactly as before, waiting on a human merge.</param>
public sealed record PublishedTaskRef(Guid TaskId, string RepositoryOwner, string RepositoryName, int PullRequestNumber, bool RequireHumanMerge);

/// <summary>One CI check's outcome (SF-614), normalized from either a GitHub Actions check run or a legacy
/// commit status into the same shape. <see cref="Conclusion"/> is one of <see cref="PullRequestCiStatus.Pending"/>,
/// <see cref="PullRequestCiStatus.Success"/>, or <see cref="PullRequestCiStatus.Failure"/>.</summary>
public sealed record PullRequestCheck(string Name, string Conclusion, string? Url);

/// <summary>CI check status for a pull request, fetched together with the exact commit GitHub reports as its
/// current head (SF-614) — so a check result can never be attributed to a different, possibly stale, commit
/// than the one it actually ran against. <see cref="Succeeded"/> false means the check data itself could not
/// be read (authentication, network, missing permissions) — reported via <see cref="Error"/> explicitly, never
/// conflated with "no checks configured" (an empty, successful <see cref="Checks"/> list).</summary>
public sealed record PullRequestChecksResult(bool Succeeded, string? HeadSha, IReadOnlyList<PullRequestCheck> Checks, string? Error);

/// <summary>The most recently synchronized CI status for a task's published pull request (SF-614) — always
/// fully overwritten by the latest poll, never merged with a previous one, so a status can never survive
/// alongside a newer head commit than the one it was actually fetched for.</summary>
public sealed record TaskCiStatus(Guid TaskId, string OverallStatus, string? HeadCommit, IReadOnlyList<PullRequestCheck> Checks, string? Error, DateTimeOffset SyncedAt);

/// <summary>Derives one overall status from a <see cref="PullRequestChecksResult"/> (SF-614) — pure and
/// independently testable, the single place this decision is made so the sync worker and the API/dashboard can
/// never disagree about what a given set of checks means.</summary>
public static class PullRequestCiStatus
{
    public const string Pending = "Pending", Success = "Success", Failure = "Failure", NoChecks = "NoChecks", Unavailable = "Unavailable";

    public static string Overall(PullRequestChecksResult result) =>
        !result.Succeeded ? Unavailable
        : result.Checks.Count == 0 ? NoChecks
        : result.Checks.Any(c => c.Conclusion == Failure) ? Failure
        : result.Checks.Any(c => c.Conclusion == Pending) ? Pending
        : Success;
}

/// <summary>Small, attribution-explicit outcome and review-effort metrics over a rolling window since
/// <paramref name="Since"/> (SF-617). Deliberately excludes lines changed and consumed quota as productivity
/// signals, and exposes no "remaining quota" figure — no real provider evidence exists at the granularity a
/// report like this needs (subscription CLIs report detected/reset-time, never a numeric remaining budget), so
/// it is omitted entirely rather than approximated. <paramref name="Retries"/> counts every re-entry into
/// <see cref="FactoryTaskStatus.Pending"/> from a non-null prior status other than <see cref="FactoryTaskStatus.WaitingForQuota"/>
/// (an automatic repair reschedule, an operator Retry, a resolved <see cref="FactoryTaskStatus.NeedsHuman"/>, or
/// an SF-613 continuation) — a quota resume is counted separately, under <paramref name="QuotaWaitingEvents"/>,
/// since resuming stalled work is not the same signal as retrying failed work.</summary>
public sealed record OutcomeMetrics(
    DateTimeOffset Since,
    int ValidatedReadyForReview,
    int MergedAccepted,
    int Rejected,
    int Retries,
    int QuotaWaitingEvents,
    int HumanInterventions,
    int AgentProcessSuccesses,
    int AgentProcessFailures,
    int CiSuccesses,
    int CiFailures,
    double? AverageReviewMinutes,
    int ReviewedTaskCount);

/// <summary>A resting task with a recorded worktree, identified well enough for <see cref="WorktreeCleanupPolicy"/>
/// to decide whether to remove it and for <see cref="IWorktreeManager"/> to remove it.</summary>
public sealed record WorktreeCleanupCandidate(Guid TaskId, FactoryTaskStatus Status, string WorktreePath, string RepositoryOwner, string RepositoryName);

/// <summary>Where a task sits in its bounded implementation-attempt budget, for both enforcement and for
/// telling the agent which attempt this is. <paramref name="Feedback"/> is the most recent operator feedback
/// recorded for this task (SF-613), if any — surfaced so an explicit correction or manual-test failure the
/// operator attached actually reaches the next generated context, not just the prior attempt's own evidence.</summary>
public sealed record AttemptContext(int Number, int MaxAttempts, PreviousAttemptSummary? Previous, TaskFeedback? Feedback = null);

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
/// <param name="QuotaSignatures">Signatures identifying a short-cooldown quota exhaustion (<see cref="QuotaWindow.ShortTerm"/>).</param>
/// <param name="WeeklyQuotaSignatures">Signatures identifying a longer, weekly-scale exhaustion (<see cref="QuotaWindow.Weekly"/>),
/// checked before <paramref name="QuotaSignatures"/> so a CLI that reports both kinds is classified correctly.
/// <see langword="null"/> or empty if this CLI is not known to report one.</param>
/// <param name="WeeklyQuotaCooldownHours">The bounded backoff used for a weekly signal with no parseable explicit
/// reset (an estimate, never presented as a value the CLI reported).</param>
/// <param name="QuotaResetPattern">An optional regular expression, with a named capture group <c>value</c>, that
/// extracts a structured reset expression (an absolute timestamp, or a relative duration like "5h" or "2 days")
/// from a matched signature's surrounding text. Left <see langword="null"/>, quota resets are always estimated
/// from <see cref="QuotaCooldownHours"/>/<paramref name="WeeklyQuotaCooldownHours"/> rather than parsed.</param>
public sealed record AgentProfile(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    string PromptDelivery,
    int TimeoutMinutes,
    IReadOnlyList<string> QuotaSignatures,
    IReadOnlyList<string> VersionArguments,
    int AvailabilityTimeoutSeconds,
    int QuotaCooldownHours,
    IReadOnlyList<string>? WeeklyQuotaSignatures = null,
    int WeeklyQuotaCooldownHours = 168,
    string? QuotaResetPattern = null);
