using System.Text.Json.Serialization;

namespace Factory.Core;

public enum FactoryTaskStatus
{
    Pending, Claimed, Preparing, Planning, Implementing, Validating, Reviewing,
    ReadyForPublish, Published, WaitingForQuota, NeedsHuman, Completed, Rejected, Failed, Stopping, Cancelled
}

public enum TaskCancellationOutcome { Stopping, Cancelled, NotCancellable }

public enum ExecutionStatus { Pending, Running, Succeeded, Failed, Cancelled }

/// <summary>The result of attempting to record a dependency edge (SF-611).</summary>
public enum AddDependencyOutcome { Added, AlreadyExists, WouldCreateCycle, TaskNotFound, SelfDependency }

/// <summary>One prerequisite <paramref name="DependsOnTaskId"/> must reach <see cref="FactoryTaskStatus.Completed"/>
/// before <paramref name="TaskId"/> becomes claimable — <see cref="DependsOnTitle"/>/<see cref="DependsOnStatus"/>
/// are read alongside the edge so the operator can see what is blocking a task without a second lookup.
/// <paramref name="Source"/> is <c>"issue"</c> when SF-710 parsed this edge from the dependent task's GitHub issue
/// body, or <see langword="null"/> when an operator added it manually through the dashboard (SF-611).
/// Historical rows may retain other source values from retired ingestion paths.</summary>
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

/// <param name="ResumableSessionId">The provider session id this task's most recent invocation reported, paired
/// with <paramref name="ResumableSessionAgent"/> (SF-701) — <see langword="null"/> if that invocation's agent
/// does not support or report one. Only ever resumed by a following invocation of the *same* agent; a different
/// agent ignores it entirely rather than risk a private session format it cannot use.</param>
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
    string? FailureReason,
    string? ResumableSessionId = null,
    string? ResumableSessionAgent = null,
    string? PreferredAgentReason = null,
    string? AgentRoutingError = null,
    string? TaskClass = null);

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
    bool CountsAsImplementationAttempt = true, string? ProviderSessionId = null, string? Model = null,
    string? ReasoningEffort = null, string? SelectionReason = null, string Purpose = "Implement", string? TaskClass = null);

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

/// <summary>Whether an <see cref="IAgentRunner"/> invocation is implementing the task (the default, and the only
/// purpose that existed before SF-702) or performing a bounded, opt-in second-agent review pass over already
/// committed work. <see cref="CliAgentRunner"/> uses this to choose the prompt it sends and which result file
/// (<c>.factory/result.json</c> vs. <c>.factory/review.json</c>) it reads back.</summary>
public enum AgentRunPurpose { Implement, Review }

/// <param name="ResumeSessionId">The provider session id to resume (SF-701), if the selected agent matches the
/// one <see cref="FactoryTask.ResumableSessionAgent"/> recorded and that agent's <see cref="AgentProfile.SupportsSessionResume"/>
/// is enabled — <see langword="null"/> for a fresh session, exactly as before this task.</param>
/// <param name="Purpose">Implement (default) or Review (SF-702) — see <see cref="AgentRunPurpose"/>.</param>
public sealed record AgentRunRequest(Guid TaskId, Guid RunId, Guid StepId, string WorkingDirectory, int AttemptNumber, string? LogPath = null, string? ResumeSessionId = null, AgentRunPurpose Purpose = AgentRunPurpose.Implement, string? TaskClass = null);

/// <param name="Window">The classified reset window a detected quota signal falls into; <see cref="QuotaWindow.None"/>
/// when <paramref name="QuotaDetected"/> is <see langword="false"/>. See <see cref="QuotaClassifier"/>.</param>
/// <param name="ResetKind">How confidently <paramref name="QuotaResetAt"/> is known.</param>
/// <param name="QuotaDetail">The short, configured signature string that triggered detection, if any — never the
/// full process output, which is preserved separately.</param>
/// <param name="ProviderSessionId">The session id this invocation's own output reported (SF-701), extracted via
/// <see cref="AgentProfile.SessionIdPattern"/> — <see langword="null"/> if the profile has session resume
/// disabled, has no pattern configured, or none was found in this invocation's output.</param>
/// <param name="ReviewResult">Set instead of <paramref name="Result"/> when this invocation's <see cref="AgentRunRequest.Purpose"/>
/// was <see cref="AgentRunPurpose.Review"/> (SF-702) — always <see langword="null"/> for an implementation invocation.</param>
public sealed record AgentRunResult(ProcessResult Process, AgentResult? Result, string? ValidationError, bool QuotaDetected,
    DateTimeOffset? QuotaResetAt = null, QuotaWindow Window = QuotaWindow.None, QuotaResetKind ResetKind = QuotaResetKind.None,
    string? QuotaDetail = null, string? ProviderSessionId = null, AgentReviewResult? ReviewResult = null,
    string? Model = null, string? ReasoningEffort = null);

public sealed record AgentAvailability(string Agent, bool Available, string? Version, string? Error);

public enum GitHubAvailabilityState { Available, Unavailable, Unknown }

public sealed record GitHubAvailability(GitHubAvailabilityState State, string? Error);

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

/// <summary>One issue a review invocation (SF-702) flagged about already-committed work. <see cref="File"/>/<see cref="Line"/>
/// are optional — a finding about overall approach rather than one specific location has neither.</summary>
public sealed record ReviewFinding(string Severity, string? File, int? Line, string Description);

/// <summary>The shape a review invocation must write to <c>.factory/review.json</c> (SF-702) — separate from
/// <see cref="AgentResult"/>/<c>.factory/result.json</c> because a review reports findings about work already
/// done, not new work of its own. An empty <see cref="Findings"/> list is a valid, useful outcome: it means the
/// review ran and found nothing worth flagging, not that no review happened.</summary>
public sealed record AgentReviewResult(string Status, string Summary, IReadOnlyList<ReviewFinding> Findings, bool NeedsHuman, string? HumanReason);

/// <summary>The single source of truth for <c>.factory/review.json</c>'s accepted <see cref="AgentReviewResult.Status"/>
/// values (SF-702), mirroring <see cref="AgentResultContract"/>.</summary>
public static class AgentReviewResultContract
{
    public static readonly IReadOnlyList<string> Statuses = ["completed", "failed", "blocked", "needs-human"];
}

/// <summary>One review finding as persisted (SF-702): <see cref="ReviewFinding"/> plus the provenance an operator
/// needs to act on it — which task and run produced it, which agent, and when.</summary>
public sealed record PersistedReviewFinding(Guid Id, Guid TaskId, Guid RunId, string Agent, string Severity, string? File, int? Line, string Description, DateTimeOffset CreatedAt);

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
/// <param name="SmokeTest">Opt-in local browser smoke-test configuration (SF-703), parsed from
/// <c>.factory/config.json</c>'s <c>smokeTest</c> key. <see langword="null"/> (the default — absent from the
/// file) means <c>SmokeTestStep</c> is skipped entirely; a repository must explicitly configure this to start a
/// local server and launch a browser at all.</param>
public sealed record RepositoryConfiguration(string BaseBranch, IReadOnlyList<ValidationCommand> BuildCommands, IReadOnlyList<ValidationCommand> TestCommands,
    int MaxImplementationAttempts, int MaxReviewAttempts, bool RequireHumanMerge, string Publish = "manual", int MaxQuotaInterruptions = 20,
    SmokeTestConfiguration? SmokeTest = null)
{
    public static RepositoryConfiguration Default { get; } =
        new("main", [new ValidationCommand("dotnet", ["build"])], [new ValidationCommand("dotnet", ["test"])], 2, 1, true, "manual", 20, null);
}

/// <summary>Opt-in configuration for SF-703's local browser smoke tests. <paramref name="InstallCommand"/>, when
/// set, runs once before <paramref name="StartCommand"/> — a task's Git worktree only ever contains tracked files
/// (e.g. a gitignored <c>node_modules</c> is never present in a freshly created worktree), so a repository whose
/// <paramref name="StartCommand"/> depends on untracked, installable dependencies must configure this to restore
/// them there first (SF-712). <paramref name="StartCommand"/> starts the repository's local application (the same
/// <see cref="ValidationCommand"/> shape build/test commands already use); <paramref name="HealthCheckUrl"/> is
/// polled until it responds successfully, or <paramref name="StartupTimeoutSeconds"/> elapses, before any check
/// runs; each of <paramref name="CheckPaths"/> (resolved against <paramref name="HealthCheckUrl"/>'s origin) is
/// then visited once, each capped at <paramref name="CheckTimeoutSeconds"/>.</summary>
public sealed record SmokeTestConfiguration(
    ValidationCommand StartCommand,
    string HealthCheckUrl,
    IReadOnlyList<string> CheckPaths,
    int StartupTimeoutSeconds = 60,
    int CheckTimeoutSeconds = 30,
    ValidationCommand? InstallCommand = null);

/// <summary>One browser check's outcome (SF-703) — a deterministic page-load check, not a full assertion
/// framework: <paramref name="Succeeded"/> means the page navigated and finished loading within its timeout.
/// <paramref name="ScreenshotPath"/> is populated on both success and failure, so a passing run still has
/// evidence, not only a failing one.</summary>
public sealed record SmokeTestCheckResult(string Path, bool Succeeded, string? Error, string? ScreenshotPath, TimeSpan Duration);

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

/// <summary>GitHub's merge calculation for the exact PR head and base observed in one read.</summary>
public sealed record PullRequestMergeResult(bool Succeeded, bool Open, string? HeadSha, string? BaseSha,
    string? Mergeable, string? MergeStateStatus, string? Error, string? HeadBranch = null, bool IsDraft = false)
{
    public string Status => !Succeeded ? "Unavailable" : !Open ? "Closed"
        : Mergeable == "CONFLICTING" || MergeStateStatus == "DIRTY" ? "Conflict"
        : Mergeable == "UNKNOWN" || MergeStateStatus == "UNKNOWN" ? "Pending"
        : MergeStateStatus is "BLOCKED" or "BEHIND" or "DRAFT" or "UNSTABLE" ? "Requirements"
        : Mergeable == "MERGEABLE" ? "Mergeable" : "Pending";
}

public sealed record TaskMergeStatus(Guid TaskId, string Status, string? HeadSha, string? BaseSha,
    string? Mergeable, string? MergeStateStatus, string? Error, DateTimeOffset SyncedAt);
public sealed record ManualMergeRequest(Guid Id, Guid TaskId, string RepositoryOwner, string RepositoryName,
    int PullRequestNumber, string BranchName, string ValidatedHeadCommit);

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
public sealed record PublishedTaskRef(Guid TaskId, string RepositoryOwner, string RepositoryName, int PullRequestNumber, bool RequireHumanMerge,
    FactoryTaskStatus Status = FactoryTaskStatus.Published);

/// <summary>One CI check's outcome (SF-614), normalized from either a GitHub Actions check run or a legacy
/// commit status into the same shape. <see cref="Conclusion"/> is one of <see cref="PullRequestCiStatus.Pending"/>,
/// <see cref="PullRequestCiStatus.Success"/>, or <see cref="PullRequestCiStatus.Failure"/>.</summary>
/// <param name="RawState">The original, un-collapsed GitHub value this check reported (e.g. <c>"FAILURE"</c>,
/// <c>"CANCELLED"</c>, <c>"ACTION_REQUIRED"</c>, <c>"TIMED_OUT"</c>) — preserved alongside the collapsed
/// three-state <see cref="Conclusion"/> specifically so <see cref="CiFailureClassifier"/> (SF-706) can tell a
/// genuine code failure apart from an infrastructure/authentication-shaped one, a distinction <see cref="Conclusion"/>
/// alone no longer carries. <see langword="null"/> for a check whose shape did not report one.</param>
public sealed record PullRequestCheck(string Name, string Conclusion, string? Url, string? RawState = null);

/// <summary>CI check status for a pull request, fetched together with the exact commit GitHub reports as its
/// current head (SF-614) — so a check result can never be attributed to a different, possibly stale, commit
/// than the one it actually ran against. <see cref="Succeeded"/> false means the check data itself could not
/// be read (authentication, network, missing permissions) — reported via <see cref="Error"/> explicitly, never
/// conflated with "no checks configured" (an empty, successful <see cref="Checks"/> list).</summary>
public sealed record PullRequestChecksResult(bool Succeeded, string? HeadSha, IReadOnlyList<PullRequestCheck> Checks, string? Error);

/// <summary>One reviewer comment or review submission with a non-empty body, observed on a still-open published
/// pull request (SF-708) — the review-comment analogue of <see cref="GitHubComment"/>, scoped to exactly what
/// ingestion needs: a stable <paramref name="CommentId"/> (a SHA-derived long from GitHub's own node id, matching
/// <c>GhCliClient</c>'s existing convention for <see cref="GitHubComment.GitHubCommentId"/>) for dedup, and enough
/// content to record as <c>factory.task_feedback</c>. <paramref name="Kind"/> is <c>"comment"</c> (top-level PR
/// conversation) or <c>"review"</c> (a formal review submission, including a change request).</summary>
public sealed record PullRequestFeedbackItem(long CommentId, string Author, string Body, DateTimeOffset CreatedAt, string Kind);

/// <summary>The most recently synchronized CI status for a task's published pull request (SF-614) — always
/// fully overwritten by the latest poll, never merged with a previous one, so a status can never survive
/// alongside a newer head commit than the one it was actually fetched for.</summary>
/// <param name="RepairTriggeredForCommit">The head commit (if any) automatic CI repair (SF-706) has already been
/// triggered for. Unlike the other fields, <c>SetCiStatusAsync</c> never overwrites this on a routine poll — only
/// <c>TriggerCiRepairAsync</c> sets it, so it survives across polls of the same commit and is what lets the
/// sync worker tell "already acted on this exact failure" apart from "a new commit's failure, never seen before."</param>
public sealed record TaskCiStatus(Guid TaskId, string OverallStatus, string? HeadCommit, IReadOnlyList<PullRequestCheck> Checks, string? Error, DateTimeOffset SyncedAt, string? RepairTriggeredForCommit = null);

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

/// <summary>Classifies a <see cref="PullRequestChecksResult"/> that has already failed (SF-706) as <see cref="ValidationFailureKind.Repairable"/>
/// or <see cref="ValidationFailureKind.Operational"/>, mirroring <see cref="ValidationFailureClassifier"/>'s
/// conservative default (repairable unless clearly not) but working from GitHub's own per-check state rather than
/// raw process output — a CI check's stdout/stderr is never fetched, only its name/conclusion/URL. The read
/// itself failing (<see cref="PullRequestChecksResult.Succeeded"/> false — <c>gh</c> authentication, network, a
/// missing permission) is always <see cref="ValidationFailureKind.Operational"/>: there is no code failure to
/// even look at. Otherwise, a failure is <see cref="ValidationFailureKind.Operational"/> only when every failing
/// check's <see cref="PullRequestCheck.RawState"/> is one GitHub itself reports for a run that never genuinely
/// executed the code under test (cancelled, timed out, needs a workflow approval, failed to start, or a legacy
/// commit-status "error") — a repairable code failure ("FAILURE"/legacy "FAILURE") anywhere in the set is enough
/// to call the whole thing repairable, since a real fix is still worth attempting.</summary>
public static class CiFailureClassifier
{
    private static readonly HashSet<string> OperationalRawStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "CANCELLED", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE", "STALE", "ERROR"
    };

    public static ValidationFailureKind Classify(PullRequestChecksResult result)
    {
        if (!result.Succeeded) return ValidationFailureKind.Operational;
        var failing = result.Checks.Where(c => c.Conclusion == PullRequestCiStatus.Failure).ToList();
        if (failing.Count == 0) return ValidationFailureKind.Repairable;
        return failing.All(c => c.RawState is not null && OperationalRawStates.Contains(c.RawState))
            ? ValidationFailureKind.Operational
            : ValidationFailureKind.Repairable;
    }
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

/// <summary>One task's most recent settling transition (<c>Completed</c>, <c>Rejected</c>, or <c>Failed</c>) in a digest
/// window — "finished work" is windowed by when a transition actually happened rather than deduplicated against a
/// previous digest, since the window itself never overlaps a prior one.</summary>
public sealed record DigestFinishedTask(Guid TaskId, string Title, string Repository, int? IssueNumber, string? PullRequestUrl, bool Merged, DateTimeOffset FinishedAt, bool Failed = false);

/// <summary>The highest-priority task the orchestrator could claim at digest generation time: Pending, not repair-paused,
/// with all dependencies complete, and below the configured review-backlog cap.</summary>
public sealed record DigestNextTask(Guid TaskId, string Title, string Repository, int? IssueNumber, int Priority, DateTimeOffset CreatedAt, string? Url = null);

/// <summary>Retry transitions during one half-open digest window, grouped by task for useful follow-up links.</summary>
public sealed record DigestRetryTask(Guid TaskId, string Title, string Repository, int? IssueNumber, string Status, int RetryCount);
public sealed record DigestRetrySummary(int TotalRetries, IReadOnlyList<DigestRetryTask> Tasks);

/// <summary>One configured provider's live CLI availability and persisted quota state at the digest's generation time.</summary>
public sealed record DigestProviderStatus(string Provider, string Availability, string? Version, string? Error,
    bool QuotaDetected, DateTimeOffset? QuotaResetAt, string? QuotaWindow, string? ResetKind, DateTimeOffset CheckedAt);

/// <summary>Counts for the completed time window, plus the net difference in currently open attention since the prior digest.</summary>
public sealed record DigestChanges(int Merged, int Rejected, int Failed, int Retries, int? OpenAttentionDelta, int? ProviderStateChanges = null);

/// <summary>A ranked, newly surfaced item requiring attention. Lower priority numbers are shown first.</summary>
public sealed record DigestActionItem(string Key, string Kind, string Title, string Detail, Guid? TaskId, string? Url, int Priority, DateTimeOffset UpdatedAt);

/// <summary>One currently-open, actionable condition a digest may surface (SF-705): a CI failure on a published
/// pull request, a task resting in <see cref="FactoryTaskStatus.NeedsHuman"/>, a failed/rejected task, or an
/// operational blocker.
/// <paramref name="Key"/> identifies the same underlying condition across digest generations (e.g.
/// <c>"ci:{taskId}"</c>, <c>"human:{taskId}"</c>, <c>"quota:{agent}"</c>, <c>"pause:{scope}"</c>) so
/// <see cref="DigestBuilder"/> can tell a still-open, unchanged condition (suppressed — see
/// <see cref="DigestPayload"/>) from one that is new or has changed (surfaced); <paramref name="Detail"/> is what
/// actually gets fingerprinted for that comparison.</summary>
public sealed record DigestAlertCandidate(string Kind, string Key, string Title, string Detail, Guid? TaskId, string? Url, DateTimeOffset UpdatedAt);

/// <summary>The deterministic operator briefing persisted for one digest generation. Outcomes and retry transitions
/// use the half-open <paramref name="WindowSince"/>–<paramref name="WindowUntil"/> window; alert lists contain only
/// new or changed conditions, while their totals describe current state. Summary, provider snapshot, next eligible
/// task, ranked actions, and plain-text briefing are included for the dashboard and configured webhook.</summary>
public sealed record DigestPayload(
    DateTimeOffset WindowSince, DateTimeOffset WindowUntil,
    IReadOnlyList<DigestFinishedTask> FinishedWork,
    IReadOnlyList<DigestAlertCandidate> CiFailures, int CiFailureTotal,
    IReadOnlyList<DigestAlertCandidate> NeedsHuman, int NeedsHumanTotal,
    IReadOnlyList<DigestAlertCandidate> Blockers, int BlockerTotal,
    IReadOnlyList<DigestAlertCandidate>? FailedTasks = null,
    int FailedTaskTotal = 0,
    DigestNextTask? NextEligibleTask = null,
    DigestRetrySummary? RetrySummary = null,
    IReadOnlyList<DigestProviderStatus>? Providers = null,
    DigestChanges? ChangesSincePrevious = null,
    IReadOnlyList<DigestActionItem>? ActionItems = null,
    string? BriefingText = null);

/// <summary>One persisted digest generation (SF-705) — what <see cref="IDigestStore.SaveAsync"/> records and
/// <see cref="IDigestStore.GetLatestAsync"/>/<see cref="IDigestStore.GetRecentAsync"/> read back.
/// <paramref name="DeliveryTarget"/>/<paramref name="DeliveryError"/> are set only when an external destination
/// was actually configured and attempted (SF-705's external-delivery requirement); a digest with no configured
/// destination is still fully generated and persisted, just never attempted.</summary>
public sealed record DigestRun(Guid Id, DateTimeOffset GeneratedAt, DigestPayload Payload, bool Delivered, string? DeliveryTarget, string? DeliveryError);

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
/// <param name="QuotaSignatures">Signatures identifying a short-cooldown quota exhaustion (<see cref="QuotaWindow.ShortTerm"/>);
/// null or empty means automatic quota classification is not configured.</param>
/// <param name="WeeklyQuotaSignatures">Signatures identifying a longer, weekly-scale exhaustion (<see cref="QuotaWindow.Weekly"/>),
/// checked before <paramref name="QuotaSignatures"/> so a CLI that reports both kinds is classified correctly.
/// <see langword="null"/> or empty if this CLI is not known to report one.</param>
/// <param name="WeeklyQuotaCooldownHours">The bounded backoff used for a weekly signal with no parseable explicit
/// reset (an estimate, never presented as a value the CLI reported).</param>
/// <param name="QuotaResetPattern">An optional regular expression, with a named capture group <c>value</c>, that
/// extracts a structured reset expression (an absolute timestamp, or a relative duration like "5h" or "2 days")
/// from a matched signature's surrounding text. Left <see langword="null"/>, quota resets are always estimated
/// from <see cref="QuotaCooldownHours"/>/<paramref name="WeeklyQuotaCooldownHours"/> rather than parsed.</param>
/// <param name="SupportsSessionResume">Whether this profile's CLI both reports a session id
/// <see cref="SessionIdPattern"/> can extract and can resume one via <see cref="ResumeArguments"/> (SF-701).
/// Defaults to <see langword="false"/>: resuming is real, CLI-documented functionality (verified directly against
/// the installed <c>codex</c>/<c>claude</c> CLIs for this task), but capturing a session id changes what actually
/// reaches this invocation's stdout — Claude's session id is only reported under <c>--output-format json</c>,
/// replacing its today's human-readable plain-text log with a JSON blob; Codex's own plain-text banner already
/// includes its session id for free, but <c>codex exec resume</c> itself has no equivalent to <c>--approve-for-me</c>,
/// only the strictly more dangerous <c>--dangerously-bypass-approvals-and-sandbox</c>. Both are real trade-offs an
/// operator should make deliberately per profile, not a change this default silently applies.</param>
/// <param name="ResumeArguments">The argument list to use instead of <see cref="Arguments"/> when resuming a
/// session, with the literal token <c>{SESSION_ID}</c> replaced by the id to resume. Required (and only used)
/// when <see cref="SupportsSessionResume"/> is <see langword="true"/>.</param>
/// <param name="SessionIdPattern">An optional regular expression, with a named capture group <c>sessionId</c>,
/// matched against this invocation's stdout to extract the provider's own session/thread id. Only consulted when
/// <see cref="SupportsSessionResume"/> is <see langword="true"/>.</param>
/// <param name="Provider">The underlying subscription/CLI this profile draws from — e.g. "Codex" or "Claude"
/// (SF-704). Defaults to <see cref="Name"/> when unset. Class-specific model settings stay within one provider
/// profile, while quota and pause remain keyed by provider.</param>
public sealed record AgentProfile(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    string PromptDelivery,
    int TimeoutMinutes,
    IReadOnlyList<string>? QuotaSignatures,
    IReadOnlyList<string> VersionArguments,
    int AvailabilityTimeoutSeconds,
    int QuotaCooldownHours,
    IReadOnlyList<string>? WeeklyQuotaSignatures = null,
    int WeeklyQuotaCooldownHours = 168,
    string? QuotaResetPattern = null,
    bool SupportsSessionResume = false,
    IReadOnlyList<string>? ResumeArguments = null,
    string? SessionIdPattern = null,
    string? Provider = null,
    string? Model = null,
    string? ReasoningEffort = null,
    bool AllowAutomaticFallback = true,
    IReadOnlyList<AgentClassProfile>? Classes = null)
{
    /// <summary>Configuration binding constructor. Defaults let a profile omit optional settings such as quota
    /// signatures without the binder trying to construct the positional record from a missing constructor value.</summary>
    public AgentProfile() : this(string.Empty, string.Empty, Array.Empty<string>(), "stdin", 90,
        Array.Empty<string>(), Array.Empty<string>(), 5, 5) { }

    /// <summary>The effective provider key for quota/pause/availability grouping — <see cref="Provider"/> if set,
    /// otherwise <see cref="Name"/> (SF-704).</summary>
    public string EffectiveProvider => Provider ?? Name;
}

public sealed record AgentClassProfile(string TaskClass, string Model, string? ReasoningEffort,
    IReadOnlyList<string> Arguments, IReadOnlyList<string>? ResumeArguments = null);
