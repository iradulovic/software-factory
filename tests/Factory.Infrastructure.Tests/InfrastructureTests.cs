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

    private static FactoryTask NewTask(string title, int issue) => new(Guid.NewGuid(), 1, 2, issue, title, "", "GitHubIssue", 0,
        FactoryTaskStatus.Pending, null, "main", null, null, null, null, null, DateTimeOffset.UtcNow, null, null, null, null);

    private sealed class StubCache : IRepositoryCache { public Task<string> PrepareAsync(GitHubRepository repository, CancellationToken cancellationToken) => Task.FromResult("cache"); }
    private sealed class StubRunner : IProcessRunner { public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken) => throw new NotSupportedException(); }
}
