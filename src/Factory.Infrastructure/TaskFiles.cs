using System.Text.Json;
using Factory.Core;

namespace Factory.Infrastructure;

public sealed class RepositoryConfigurationReader : IRepositoryConfigurationReader
{
    public async Task<RepositoryConfiguration> ReadAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(worktreePath, ".factory", "config.json");
        if (!File.Exists(path)) return RepositoryConfiguration.Default;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RepositoryConfiguration>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
            ?? RepositoryConfiguration.Default;
    }
}

public sealed class TaskContextWriter : ITaskContextWriter
{
    public async Task WriteAsync(string worktreePath, GitHubRepository repository, GitHubIssue? issue, FactoryTask task, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(worktreePath, ".factory");
        Directory.CreateDirectory(directory);
        var comments = issue?.Comments.Count > 0 ? string.Join("\n\n", issue.Comments.Select(c => $"### {c.Author}\n\n{c.Body}")) : "No comments.";
        var content = $"""
            # Task

            Repository: {repository.Owner}/{repository.Name}
            GitHub issue: #{task.IssueNumber}

            ## Title

            {task.Title}

            ## Description

            {task.Description}

            ## Comments

            {comments}

            ## Constraints

            - Base branch: {task.BaseBranch}
            - Follow AGENTS.md
            - Do not modify unrelated files
            - Run relevant tests
            - Do not push or create a pull request

            ## Completion

            Write `.factory/result.json` with: status, summary, testsRun, testsPassed, filesChanged, risks, needsHuman, and humanReason.
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "task.md"), content, cancellationToken);
    }
}
