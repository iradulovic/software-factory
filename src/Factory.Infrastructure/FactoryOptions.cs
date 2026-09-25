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

    /// <summary>Server-side <c>statement_timeout</c> (seconds) applied to an operator's ad-hoc <c>/api/database/query</c>
    /// SELECT (SF-717), so one runaway join can't hang the API.</summary>
    public int DatabaseQueryTimeoutSeconds { get; set; } = 5;

    /// <summary>Maximum rows returned from an ad-hoc <c>/api/database/query</c> SELECT (SF-717); the response reports
    /// whether the result was truncated by this cap.</summary>
    public int DatabaseQueryRowLimit { get; set; } = 500;

    /// <summary>Profile reserved for judgment-heavy review invocations, independently of task implementation routing.</summary>
    public string ReviewPreferredAgent { get; set; } = CodexIssueRouter.Codex;
    public string ReviewTaskClass { get; set; } = "deep";
}

/// <summary>The configured set of CLI coding-agent profiles. Adding a provider or model preset is a configuration
/// addition here, never a new runner class.</summary>
public sealed class AgentProfilesOptions
{
    /// <summary>The default profile set used when no <c>Agents:Profiles</c> configuration section is present
    /// at all. Deliberately not <see cref="Profiles"/>'s own default value: <c>ConfigurationBinder.Get{T}</c>
    /// binds a configured <c>List{T}</c> section by appending to whatever the target list already contains
    /// rather than replacing it, so if <see cref="Profiles"/> started non-empty, an explicitly configured
    /// "Agents:Profiles" section (even one that looks identical to this default) would end up bound alongside
    /// it instead of in place of it — registering every configured agent twice.</summary>
    public static readonly IReadOnlyList<AgentProfile> DefaultProfiles =
    [
        new("Codex", "codex", [], "stdin", 90, ["quota", "usage limit"], ["--version"], 5, 5,
            SessionIdPattern: @"session id: (?<sessionId>[0-9a-fA-F-]{36})",
            Provider: "Codex",
            Classes: [
                new("quick", "gpt-5.6-luna", "max", ["exec", "-m", "gpt-5.6-luna", "-c", "model_reasoning_effort=\"max\"", "--approve-for-me", "-"], ["exec", "resume", "{SESSION_ID}", "-m", "gpt-5.6-luna", "-c", "model_reasoning_effort=\"max\"", "--dangerously-bypass-approvals-and-sandbox", "-"]),
                new("deep", "gpt-5.6-sol", "medium", ["exec", "-m", "gpt-5.6-sol", "-c", "model_reasoning_effort=\"medium\"", "--approve-for-me", "-"], ["exec", "resume", "{SESSION_ID}", "-m", "gpt-5.6-sol", "-c", "model_reasoning_effort=\"medium\"", "--dangerously-bypass-approvals-and-sandbox", "-"])
            ])
    ];

    public List<AgentProfile> Profiles { get; set; } = [];
}

public sealed class GitHubSyncOptions
{
    public int PollingIntervalSeconds { get; set; } = 60;
    public int AvailabilityTimeoutSeconds { get; set; } = 5;
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

/// <summary>Governs the digest generator (SF-705). <see cref="WebhookUrl"/> left unset means the digest is
/// generated and persisted (readable via <c>GET /api/digest</c>) but never delivered anywhere — external
/// delivery requires an explicitly configured destination, exactly like <see cref="TelemetryOptions.OtlpEndpoint"/>
/// leaving trace export off until an endpoint is configured.</summary>
public sealed class DigestOptions
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 3600;

    /// <summary>Minimum time between two generations, regardless of how often the poll loop wakes up — keeps a
    /// "daily digest" actually daily by default without needing a separate cron-like scheduler.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>Where to POST the generated digest as JSON. Left unset (the default), nothing is ever delivered
    /// externally — the digest is still generated and readable via <c>GET /api/digest</c>.</summary>
    public string? WebhookUrl { get; set; }
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
