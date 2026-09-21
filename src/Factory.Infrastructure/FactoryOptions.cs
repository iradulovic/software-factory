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
    public List<AgentProfile> Profiles { get; set; } =
    [
        new("Codex", "codex", ["exec", "--full-auto", "-"], "stdin", 90, ["quota", "usage limit"], ["--version"], 5, 5)
    ];
}

public sealed class GitHubSyncOptions
{
    public int PollingIntervalSeconds { get; set; } = 60;
    public List<ConfiguredRepository> Repositories { get; set; } = [];
}

public sealed record ConfiguredRepository(string Owner, string Name, string CloneUrl, string DefaultBranch = "main", bool Enabled = true);
