using Factory.Core;

namespace Factory.Infrastructure.Tests;

/// <summary>Covers the completion contract generated into .factory/task.md (SF-605): every documented status,
/// the commit requirement, and a JSON example that actually round-trips through AgentResultReader's own parser.</summary>
public sealed class TaskContextWriterTests
{
    private static readonly GitHubRepository Repository = new(1, "acme", "billing", "url", "main", true);
    private static readonly FactoryTask Task = new(Guid.NewGuid(), 1, 2, 42, "Add invoice export", "Export invoices as CSV.", "GitHubIssue", 0,
        FactoryTaskStatus.Implementing, null, "main", null, null, "worker", null, null, DateTimeOffset.UtcNow, null, null, null, null);

    [Fact]
    public async Task Generated_task_file_requires_a_commit_before_finishing()
    {
        var content = await WriteAndReadAsync();
        Assert.Contains("Commit every intended change on this branch before finishing", content);
        Assert.Contains("uncommitted work cannot be validated or published", content);
    }

    [Fact]
    public async Task Generated_task_file_documents_every_accepted_status()
    {
        var content = await WriteAndReadAsync();
        foreach (var status in AgentResultContract.Statuses)
            Assert.Contains($"\"{status}\"", content);
    }

    [Fact]
    public async Task Generated_task_file_documents_every_required_field()
    {
        var content = await WriteAndReadAsync();
        foreach (var field in new[] { "status", "summary", "testsRun", "testsPassed", "filesChanged", "risks", "needsHuman", "humanReason" })
            Assert.Contains($"`{field}`", content);
    }

    [Fact]
    public async Task Generated_example_is_valid_json_that_the_real_reader_accepts()
    {
        var root = Directory.CreateTempSubdirectory("factory-context-");
        try
        {
            await new TaskContextWriter().WriteAsync(root.FullName, Repository, null, Task,
                new AttemptContext(1, 2, null), CancellationToken.None);
            var content = await File.ReadAllTextAsync(Path.Combine(root.FullName, ".factory", "task.md"));

            var exampleStart = content.IndexOf("```json", StringComparison.Ordinal);
            var exampleEnd = content.IndexOf("```", exampleStart + 7, StringComparison.Ordinal);
            Assert.True(exampleStart >= 0 && exampleEnd > exampleStart, "Expected a fenced JSON example in the generated file.");
            var example = content[(exampleStart + 7)..exampleEnd].Trim();

            await File.WriteAllTextAsync(Path.Combine(root.FullName, ".factory", "result.json"), example);
            var (result, error) = await new AgentResultReader().ReadAsync(root.FullName, CancellationToken.None);
            Assert.Null(error);
            Assert.NotNull(result);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task Stale_result_from_an_earlier_attempt_is_deleted_before_writing_the_new_context()
    {
        var root = Directory.CreateTempSubdirectory("factory-context-");
        try
        {
            var factoryDir = Directory.CreateDirectory(Path.Combine(root.FullName, ".factory"));
            var resultPath = Path.Combine(factoryDir.FullName, "result.json");
            await File.WriteAllTextAsync(resultPath, "{\"status\":\"failed\"}");

            await new TaskContextWriter().WriteAsync(root.FullName, Repository, null, Task,
                new AttemptContext(2, 2, null), CancellationToken.None);

            Assert.False(File.Exists(resultPath));
        }
        finally { root.Delete(true); }
    }

    private static async Task<string> WriteAndReadAsync()
    {
        var root = Directory.CreateTempSubdirectory("factory-context-");
        try
        {
            await new TaskContextWriter().WriteAsync(root.FullName, Repository, null, Task,
                new AttemptContext(1, 2, null), CancellationToken.None);
            return await File.ReadAllTextAsync(Path.Combine(root.FullName, ".factory", "task.md"));
        }
        finally { root.Delete(true); }
    }
}
