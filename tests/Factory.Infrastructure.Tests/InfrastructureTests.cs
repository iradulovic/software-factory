using System.Text.Json;
using Factory.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Infrastructure.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void Worktree_location_is_deterministic_and_safe()
    {
        var manager = new GitWorktreeManager(new StubCache(), new StubRunner(), Options.Create(new FactoryOptions { RootDirectory = "C:/factory" }), NullLogger<GitWorktreeManager>.Instance);
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
            var manager = new GitWorktreeManager(new StubCache(), runner, options, NullLogger<GitWorktreeManager>.Instance);
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
    public async Task Availability_checker_reports_unauthenticated_when_authentication_check_fails()
    {
        var runner = new SequenceResultRunner(
            Successful("codex 1.2.3\n"),
            new ProcessResult("codex", ["login", "status"], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                1, "", "Not logged in", false, false));
        var checker = new CliAgentAvailabilityChecker(DefaultProfile, runner);

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.False(availability.Available);
        Assert.Equal("codex 1.2.3", availability.Version);
        Assert.Contains("Authentication check failed", availability.Error);
        Assert.Contains("re-authenticate", availability.Error);
    }

    [Fact]
    public async Task Availability_checker_reports_authentication_timeout_distinctly()
    {
        var runner = new SequenceResultRunner(
            Successful("codex 1.2.3\n"),
            new ProcessResult("codex", ["login", "status"], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                null, "", "", true, false));
        var checker = new CliAgentAvailabilityChecker(DefaultProfile, runner);

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.False(availability.Available);
        Assert.Contains("Authentication check timed out", availability.Error);
    }

    [Fact]
    public async Task Availability_checker_reports_missing_executable_when_authentication_command_cannot_start()
    {
        var runner = new MissingOnSecondRunner();
        var checker = new CliAgentAvailabilityChecker(DefaultProfile, runner);

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.False(availability.Available);
        Assert.Equal("Executable not found", availability.Error);
    }

    [Fact]
    public async Task Availability_checker_caches_the_combined_result_briefly()
    {
        var runner = new SequenceResultRunner(Successful("codex 1.2.3\n"), Successful());
        var checker = new CliAgentAvailabilityChecker(DefaultProfile, runner);

        Assert.True((await checker.CheckAsync(CancellationToken.None)).Available);
        Assert.True((await checker.CheckAsync(CancellationToken.None)).Available);

        Assert.Equal(2, runner.CallCount);
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

    [Fact]
    public async Task GitHub_availability_checker_reports_available_without_exposing_account_output()
    {
        var runner = new StubResultRunner(new ProcessResult("gh", ["api", "user"], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            0, "factory-bot\n", "", false, false));
        var checker = new GitHubAvailabilityChecker(runner, Options.Create(new GitHubSyncOptions()));

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(GitHubAvailabilityState.Available, availability.State);
        Assert.Null(availability.Error);
    }

    [Fact]
    public async Task GitHub_availability_checker_sanitizes_authentication_failures()
    {
        var runner = new StubResultRunner(new ProcessResult("gh", ["api", "user"], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            1, "", "HTTP 401: token ghp_secret", false, false));
        var checker = new GitHubAvailabilityChecker(runner, Options.Create(new GitHubSyncOptions()));

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(GitHubAvailabilityState.Unavailable, availability.State);
        Assert.Equal("GitHub CLI authentication or API check failed", availability.Error);
        Assert.DoesNotContain("ghp_secret", availability.Error);
    }

    [Fact]
    public async Task GitHub_availability_checker_reports_timeout_as_unavailable()
    {
        var runner = new StubResultRunner(new ProcessResult("gh", ["api", "user"], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, "", "", true, false));
        var checker = new GitHubAvailabilityChecker(runner, Options.Create(new GitHubSyncOptions()));

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(GitHubAvailabilityState.Unavailable, availability.State);
        Assert.Equal("GitHub availability check timed out", availability.Error);
    }

    [Fact]
    public async Task GitHub_availability_checker_reports_missing_cli_as_unavailable()
    {
        var checker = new GitHubAvailabilityChecker(new ThrowingRunner(), Options.Create(new GitHubSyncOptions()));

        var availability = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(GitHubAvailabilityState.Unavailable, availability.State);
        Assert.Equal("GitHub CLI executable not found", availability.Error);
    }

    [Fact]
    public async Task GitHub_availability_checker_leaves_unexpected_errors_for_the_api_to_classify()
    {
        var checker = new GitHubAvailabilityChecker(new UnexpectedRunner(), Options.Create(new GitHubSyncOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => checker.CheckAsync(CancellationToken.None));
    }

    private static readonly AgentProfile DefaultProfile = new("Codex", "codex", ["exec", "--full-auto", "-"], "stdin", 90, ["quota", "usage limit"], ["--version"], 5, 5)
    {
        AuthenticationArguments = ["login", "status"]
    };

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

    private sealed class SequenceResultRunner(params ProcessResult[] results) : IProcessRunner
    {
        private int index;
        public int CallCount => index;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(results[Math.Min(index++, results.Length - 1)]);
    }

    private sealed class MissingOnSecondRunner : IProcessRunner
    {
        private int callCount;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (++callCount == 2) throw new System.ComponentModel.Win32Exception("No such file or directory");
            return Task.FromResult(Successful("codex 1.2.3\n"));
        }
    }

    private static ProcessResult Successful(string output = "") =>
        new("codex", [], ".", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, output, "", false, false);

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new System.ComponentModel.Win32Exception("No such file or directory");
    }

    private sealed class UnexpectedRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("unexpected checker failure");
    }
}
