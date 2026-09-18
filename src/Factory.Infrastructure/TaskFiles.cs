using System.Text.Json;
using Factory.Core;

namespace Factory.Infrastructure;

/// <summary>
/// Reads <c>.factory/config.json</c> from a Git reference rather than from the worktree, so the validation
/// commands come from the base branch and cannot be rewritten by the agent whose work they validate.
/// </summary>
public sealed class RepositoryConfigurationReader(IProcessRunner runner) : IRepositoryConfigurationReader
{
    public const string ConfigurationPath = ".factory/config.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RepositoryConfiguration> ReadAsync(string worktreePath, string baseRef, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new ProcessRequest("git", ["show", $"{baseRef}:{ConfigurationPath}"], worktreePath, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (result.Succeeded) return Parse(result.StandardOutput, baseRef);
        if (result.ExitCode == 128 && (result.StandardError.Contains("does not exist in", StringComparison.Ordinal) || result.StandardError.Contains("exists on disk, but not in", StringComparison.Ordinal)))
            return RepositoryConfiguration.Default;
        throw new InvalidOperationException($"Reading {ConfigurationPath} from {baseRef} failed: {result.StandardError.Trim()}");
    }

    public static RepositoryConfiguration Parse(string json, string source)
    {
        RepositoryConfigurationFile? file;
        try { file = JsonSerializer.Deserialize<RepositoryConfigurationFile>(json, JsonOptions); }
        catch (JsonException ex) { throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: {ex.Message}", ex); }

        var defaults = RepositoryConfiguration.Default;
        var baseBranch = file?.BaseBranch;
        var configuration = new RepositoryConfiguration(
            string.IsNullOrWhiteSpace(baseBranch) ? defaults.BaseBranch : baseBranch.Trim(),
            file?.BuildCommands ?? defaults.BuildCommands,
            file?.TestCommands ?? defaults.TestCommands,
            file?.MaxImplementationAttempts ?? defaults.MaxImplementationAttempts,
            file?.MaxReviewAttempts ?? defaults.MaxReviewAttempts,
            file?.RequireHumanMerge ?? defaults.RequireHumanMerge,
            string.IsNullOrWhiteSpace(file?.Publish) ? defaults.Publish : file.Publish.Trim());

        if (configuration.MaxImplementationAttempts < 1 || configuration.MaxReviewAttempts < 0)
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: maxImplementationAttempts must be at least 1 and maxReviewAttempts must not be negative.");
        if (configuration.BuildCommands.Any(string.IsNullOrWhiteSpace) || configuration.TestCommands.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: build and test commands must not be empty.");
        if (configuration.Publish is not ("manual" or "auto-draft"))
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: publish must be 'manual' or 'auto-draft'.");
        return configuration;
    }
}

internal sealed class RepositoryConfigurationFile
{
    public string? BaseBranch { get; init; }
    public IReadOnlyList<string>? BuildCommands { get; init; }
    public IReadOnlyList<string>? TestCommands { get; init; }
    public int? MaxImplementationAttempts { get; init; }
    public int? MaxReviewAttempts { get; init; }
    public bool? RequireHumanMerge { get; init; }
    public string? Publish { get; init; }
}

public sealed class TaskContextWriter : ITaskContextWriter
{
    public async Task WriteAsync(string worktreePath, GitHubRepository repository, GitHubIssue? issue, FactoryTask task, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(worktreePath, ".factory");
        Directory.CreateDirectory(directory);

        // A result left behind by an earlier attempt must never be mistaken for this attempt's output.
        var staleResult = Path.Combine(directory, "result.json");
        if (File.Exists(staleResult)) File.Delete(staleResult);

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
