using Factory.Core;

namespace Factory.Infrastructure;

public sealed class FactoryOptions
{
    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=software_factory;Username=factory;Password=factory";
    public string RootDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".software-factory");
    public int TaskConcurrency { get; set; } = 1;
    public int PollingIntervalSeconds { get; set; } = 10;
    public int TaskLeaseSeconds { get; set; } = 600;
    public int LeaseHeartbeatSeconds { get; set; } = 120;

    /// <summary>How long a claimed publication attempt (push + pull-request creation, a much shorter-lived unit of
    /// work than implementing a task) may run before <see cref="ITaskStore.ClaimNextPublicationAsync"/> treats it
    /// as abandoned and reclaims it.</summary>
    public int PublicationLeaseSeconds { get; set; } = 300;

    /// <summary>Caps outstanding review work — tasks resting in <see cref="FactoryTaskStatus.ReadyForPublish"/>
    /// or <see cref="FactoryTaskStatus.Published"/> at once — so unattended implementation can never outrun the
    /// operator's own review capacity (SF-612). Checked by <see cref="ITaskStore.ClaimNextAsync"/> against a
    /// brand-new <see cref="FactoryTaskStatus.Pending"/> claim only; an already-executing task's lease recovery,
    /// and publication/reconciliation of existing review work (which frees this count back up), are unaffected.
    /// Zero or negative disables the limit.</summary>
    public int MaxOutstandingReviewWork { get; set; } = 5;

    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>Base URL of the dashboard, used to link back to a task from the GitHub comments the orchestrator posts.</summary>
    public string DashboardBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>Where full agent and validation-command stdout/stderr are streamed while a step runs. Only
    /// bounded previews of this ever reach PostgreSQL; the API reads the full file from here on request.</summary>
    public string LogsDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".software-factory", "logs");
}

/// <summary>The configured set of CLI coding agents. Defaults to a single "Codex" profile matching the
/// bootstrap spec's original hardcoded behavior; adding "Claude" or any other CLI agent is a configuration
/// addition here, never a new class.</summary>
public sealed class AgentProfilesOptions
{
    /// <summary>The single "Codex" profile used when no <c>Agents:Profiles</c> configuration section is present
    /// at all. Deliberately not <see cref="Profiles"/>'s own default value: <c>ConfigurationBinder.Get{T}</c>
    /// binds a configured <c>List{T}</c> section by appending to whatever the target list already contains
    /// rather than replacing it, so if <see cref="Profiles"/> started non-empty, an explicitly configured
    /// "Agents:Profiles" section (even one that looks identical to this default) would end up bound alongside
    /// it instead of in place of it — registering every configured agent twice.</summary>
    public static readonly IReadOnlyList<AgentProfile> DefaultProfiles =
    [
        new("Codex", "codex", ["exec", "--approve-for-me", "-"], "stdin", 90, ["quota", "usage limit"], ["--version"], 5, 5,
            SessionIdPattern: @"session id: (?<sessionId>[0-9a-fA-F-]{36})",
            ResumeArguments: ["exec", "resume", "{SESSION_ID}", "--dangerously-bypass-approvals-and-sandbox", "-"])
    ];

    public List<AgentProfile> Profiles { get; set; } = [];
}

public sealed class GitHubSyncOptions
{
    public int PollingIntervalSeconds { get; set; } = 60;
    public List<ConfiguredRepository> Repositories { get; set; } = [];

    /// <summary>Caps how many times automatic CI repair (SF-706) will continue the same task across an unbounded
    /// sequence of distinct failing commits, so a task whose repair attempts keep producing new code that still
    /// fails CI cannot loop forever. Counted from the task's own <c>ci-repair</c>-attributed <c>task_feedback</c>
    /// rows; once reached, the task moves to <see cref="FactoryTaskStatus.NeedsHuman"/> instead of triggering
    /// another repair.</summary>
    public int MaxCiRepairAttempts { get; set; } = 2;
}

public sealed record ConfiguredRepository(string Owner, string Name, string CloneUrl, string DefaultBranch = "main", bool Enabled = true);

/// <summary>Governs the orchestrator-owned sweep that removes resting tasks' worktrees. Retention defaults to
/// keeping <see cref="FactoryTaskStatus.Failed"/> and <see cref="FactoryTaskStatus.NeedsHuman"/> around for
/// inspection; see <see cref="WorktreeCleanupPolicy"/> for the full set of statuses cleanup ever considers.</summary>
public sealed class WorktreeCleanupOptions
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 300;
    public List<FactoryTaskStatus> RetainStatuses { get; set; } = [.. WorktreeCleanupPolicy.DefaultRetainedStatuses];
}

/// <summary>Governs OpenTelemetry trace export (see <see cref="TelemetryExtensions.AddFactoryTelemetry"/>).
/// Exporting is entirely optional: with no <see cref="OtlpEndpoint"/> configured, no exporter is registered at
/// all, so a missing or unreachable collector never affects startup or the app's own health.</summary>
public sealed class TelemetryOptions
{
    /// <summary>Overrides the host's own default service name (e.g. "Factory.Api") if set.</summary>
    public string? ServiceName { get; set; }

    /// <summary>The OTLP endpoint to export traces to, e.g. "http://localhost:4317". Left unset, no exporter is added.</summary>
    public string? OtlpEndpoint { get; set; }
}
