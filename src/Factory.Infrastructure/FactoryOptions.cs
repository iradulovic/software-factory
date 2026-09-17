namespace Factory.Infrastructure;

public sealed class FactoryOptions
{
    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=software_factory;Username=factory;Password=factory";
    public string RootDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".software-factory");
    public int TaskConcurrency { get; set; } = 1;
    public int PollingIntervalSeconds { get; set; } = 10;
    public int TaskLeaseSeconds { get; set; } = 120;
    public int LeaseHeartbeatSeconds { get; set; } = 30;
    public string WorkerId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";
}

public sealed class CodexOptions
{
    public string Executable { get; set; } = "codex";
    public string[] Arguments { get; set; } = ["exec", "--full-auto", "-"];
    public int TimeoutMinutes { get; set; } = 90;
}

public sealed class GitHubSyncOptions
{
    public int PollingIntervalSeconds { get; set; } = 60;
    public List<ConfiguredRepository> Repositories { get; set; } = [];
}

public sealed record ConfiguredRepository(string Owner, string Name, string CloneUrl, string DefaultBranch = "main", bool Enabled = true);
