using System.Text.Json;
using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void Worktree_location_is_deterministic_and_safe()
    {
        var manager = new GitWorktreeManager(new StubCache(), new StubRunner(), Options.Create(new FactoryOptions { RootDirectory = "C:/factory" }));
        var repository = new GitHubRepository(1, "acme", "billing", "url", "main", true);
        var task = NewTask("Add CSV Export!!!", 142);
        var first = manager.GetLocation(repository, task);
        Assert.Equal(first, manager.GetLocation(repository, task));
        Assert.Equal("factory/142-add-csv-export", first.BranchName);
        Assert.EndsWith(Path.Combine("acme", "billing", "issue-142"), first.Path);
    }

    [Fact]
    public async Task Existing_recorded_worktree_is_reused_during_recovery()
    {
        var root = Directory.CreateTempSubdirectory("factory-worktree-");
        try
        {
            var options = Options.Create(new FactoryOptions { RootDirectory = root.FullName });
            var runner = new StubRunner();
            var manager = new GitWorktreeManager(new StubCache(), runner, options);
            var repository = new GitHubRepository(1, "acme", "billing", "url", "main", true);
            var pending = NewTask("Add CSV Export!!!", 142);
            var location = manager.GetLocation(repository, pending);
            Directory.CreateDirectory(location.Path);
            await File.WriteAllTextAsync(Path.Combine(location.Path, ".git"), "gitdir: cache/worktrees/issue-142");
            var recovered = pending with { WorktreePath = location.Path, BranchName = location.BranchName, Status = FactoryTaskStatus.Claimed };

            Assert.Equal(location, await manager.CreateAsync(repository, recovered, CancellationToken.None));
            Assert.Equal(0, runner.CallCount);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Agent_result_reader_accepts_valid_contract()
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            var value = new AgentResult("completed", "Done", ["dotnet test"], true, ["a.cs"], [], false, null);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"), JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);
            Assert.Null(error); Assert.Equal("Done", result?.Summary);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Agent_result_reader_rejects_unknown_status()
    {
        var root = Directory.CreateTempSubdirectory("factory-result-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "result.json"), "{\"status\":\"magic\",\"summary\":\"Done\",\"testsRun\":[],\"testsPassed\":false,\"filesChanged\":[],\"risks\":[],\"needsHuman\":false}");
            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);
            Assert.Null(result); Assert.Contains("Unsupported", error);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Process_result_reports_success_and_duration()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var result = new ProcessResult("dotnet", ["--version"], ".", start, start.AddSeconds(2), 0, "10.0", "", false, false);
        Assert.True(result.Succeeded); Assert.Equal(TimeSpan.FromSeconds(2), result.Duration);
        Assert.False((result with { TimedOut = true }).Succeeded);
    }

    [Fact]
    public async Task Availability_checker_reports_available_from_successful_version_check()
    {
        var runner = new StubResultRunner(new ProcessResult("codex", ["--version"], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "codex 1.2.3\n", "", false, false));
        var checker = new CliAgentAvailabilityChecker(DefaultProfile, runner);

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal("Codex", availability.Agent);
        Assert.True(availability.Available);
        Assert.Equal("codex 1.2.3", availability.Version);
        Assert.Null(availability.Error);
    }

    [Fact]
    public async Task Availability_checker_reports_unavailable_when_check_times_out()
    {
        var runner = new StubResultRunner(new ProcessResult("codex", ["--version"], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, "", "", true, false));
        var checker = new CliAgentAvailabilityChecker(DefaultProfile, runner);

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.False(availability.Available);
        Assert.Null(availability.Version);
        Assert.Contains("timed out", availability.Error);
    }

    [Fact]
    public async Task Availability_checker_reports_unavailable_when_executable_is_missing()
    {
        var checker = new CliAgentAvailabilityChecker(DefaultProfile, new ThrowingRunner());

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.False(availability.Available);
        Assert.Equal("Executable not found", availability.Error);
    }

    private static readonly AgentProfile DefaultProfile = new("Codex", "codex", ["exec", "--full-auto", "-"], "stdin", 90, ["quota", "usage limit"], ["--version"], 5, 5);

    private static FactoryTask NewTask(string title, int issue) => new(Guid.NewGuid(), 1, 2, issue, title, "", "GitHubIssue", 0,
        FactoryTaskStatus.Pending, null, "main", null, null, null, null, null, DateTimeOffset.UtcNow, null, null, null, null);

    private sealed class StubCache : IRepositoryCache
    {
        public Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken) => Task.FromResult("cache");
        public string GetPath(string owner, string name) => "cache";
    }
    private sealed class StubRunner : IProcessRunner
    {
        public int CallCount { get; private set; }
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new NotSupportedException();
        }
    }

    private sealed class StubResultRunner(ProcessResult result) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new System.ComponentModel.Win32Exception("No such file or directory");
    }
}
