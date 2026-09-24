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
            string.IsNullOrWhiteSpace(file?.Publish) ? defaults.Publish : file.Publish.Trim(),
            file?.MaxQuotaInterruptions ?? defaults.MaxQuotaInterruptions,
            ParseSmokeTest(file?.SmokeTest, source),
            file?.SerializeSameBatchTrackerTasks ?? defaults.SerializeSameBatchTrackerTasks);

        if (configuration.MaxImplementationAttempts < 1 || configuration.MaxReviewAttempts < 0)
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: maxImplementationAttempts must be at least 1 and maxReviewAttempts must not be negative.");
        if (configuration.MaxQuotaInterruptions < 1)
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: maxQuotaInterruptions must be at least 1.");
        if (configuration.BuildCommands.Any(c => string.IsNullOrWhiteSpace(c.Executable)) || configuration.TestCommands.Any(c => string.IsNullOrWhiteSpace(c.Executable)))
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: build and test commands must not be empty.");
        if (configuration.Publish is not ("manual" or "auto-draft"))
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: publish must be 'manual' or 'auto-draft'.");
        return configuration;
    }

    /// <summary>SF-703's opt-in <c>smokeTest</c> key — absent entirely means no smoke test (the common case,
    /// unlike build/test commands which always have a default), so this returns <see langword="null"/> rather
    /// than substituting any default configuration.</summary>
    private static SmokeTestConfiguration? ParseSmokeTest(SmokeTestConfigurationFile? file, string source)
    {
        if (file is null) return null;
        if (file.StartCommand is null || string.IsNullOrWhiteSpace(file.StartCommand.Executable))
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: smokeTest.startCommand must be set with a non-empty executable.");
        if (string.IsNullOrWhiteSpace(file.HealthCheckUrl))
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: smokeTest.healthCheckUrl must be set.");
        var startupTimeoutSeconds = file.StartupTimeoutSeconds ?? 60;
        var checkTimeoutSeconds = file.CheckTimeoutSeconds ?? 30;
        if (startupTimeoutSeconds < 1 || checkTimeoutSeconds < 1)
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: smokeTest.startupTimeoutSeconds and smokeTest.checkTimeoutSeconds must be at least 1.");
        if (file.InstallCommand is not null && string.IsNullOrWhiteSpace(file.InstallCommand.Executable))
            throw new InvalidOperationException($"Invalid {ConfigurationPath} in {source}: smokeTest.installCommand must have a non-empty executable.");
        return new SmokeTestConfiguration(file.StartCommand, file.HealthCheckUrl.Trim(), file.CheckPaths ?? ["/"], startupTimeoutSeconds, checkTimeoutSeconds, file.InstallCommand);
    }
}

internal sealed class RepositoryConfigurationFile
{
    public string? BaseBranch { get; init; }
    public IReadOnlyList<ValidationCommand>? BuildCommands { get; init; }
    public IReadOnlyList<ValidationCommand>? TestCommands { get; init; }
    public int? MaxImplementationAttempts { get; init; }
    public int? MaxReviewAttempts { get; init; }
    public bool? RequireHumanMerge { get; init; }
    public string? Publish { get; init; }
    public int? MaxQuotaInterruptions { get; init; }
    public SmokeTestConfigurationFile? SmokeTest { get; init; }
    public bool? SerializeSameBatchTrackerTasks { get; init; }
}

internal sealed class SmokeTestConfigurationFile
{
    public ValidationCommand? StartCommand { get; init; }
    public string? HealthCheckUrl { get; init; }
    public IReadOnlyList<string>? CheckPaths { get; init; }
    public int? StartupTimeoutSeconds { get; init; }
    public int? CheckTimeoutSeconds { get; init; }
    public ValidationCommand? InstallCommand { get; init; }
}

public sealed class TaskContextWriter : ITaskContextWriter
{
    public async Task WriteAsync(string worktreePath, GitHubRepository repository, GitHubIssue? issue, FactoryTask task, AttemptContext attempt, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(worktreePath, ".factory");
        Directory.CreateDirectory(directory);

        // A result left behind by an earlier attempt must never be mistaken for this attempt's output.
        var staleResult = Path.Combine(directory, "result.json");
        if (File.Exists(staleResult)) File.Delete(staleResult);

        var comments = issue?.Comments.Count > 0 ? string.Join("\n\n", issue.Comments.Select(c => $"### {c.Author}\n\n{c.Body}")) : "No comments.";
        // SF-707: a TASKS.md-sourced task has no GitHub issue at all (task.IssueNumber is null) — that line is
        // simply omitted rather than rendering a bare, misleading "GitHub issue: #".
        var source = task.IssueNumber is { } issueNumber ? $"GitHub issue: #{issueNumber}" : "Source: this repository's own TASKS.md tracker file";
        var content = $"""
            # Task

            Repository: {repository.Owner}/{repository.Name}
            {source}

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
            - Commit every intended change on this branch before finishing — uncommitted work cannot be validated or published
            - Do not push, create a pull request, or touch any other branch or worktree

            ## Attempt

            This is attempt {attempt.Number} of {attempt.MaxAttempts}.

            {DescribeFeedback(attempt.Feedback)}
            {DescribePreviousAttempt(attempt.Previous)}
            {CompletionContract}
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "task.md"), content, cancellationToken);
    }

    // Generated from AgentResultContract.Statuses (Factory.Core), not hand-copied, so this documentation and
    // AgentResultReader's actual validation can never silently disagree (SF-605).
    private static readonly string CompletionContract = $$"""
        ## Completion

        Write `.factory/result.json` as a single JSON object matching exactly this contract:

        | Field | Type | Required | Meaning |
        |---|---|---|---|
        | `status` | string | yes | One of {{string.Join(", ", AgentResultContract.Statuses.Select(s => $"`\"{s}\"`"))}} (see below). |
        | `summary` | string | yes, non-empty | One or two sentences describing the outcome. |
        | `testsRun` | string[] | yes (may be empty) | Test commands or suites actually run. |
        | `testsPassed` | bool | yes | Whether `testsRun` passed. |
        | `filesChanged` | string[] | yes (may be empty) | Files intentionally changed. |
        | `risks` | string[] | yes (may be empty) | Anything a human reviewer should double-check. |
        | `needsHuman` | bool | yes | `true` forces human review regardless of `status`. |
        | `humanReason` | string or null | only when `needsHuman` is `true`, or `status` is `"blocked"`/`"needs-human"` | Why a human is needed. |

        - `"completed"`: the task was implemented and its changes committed on this branch; independent build/test validation runs next.
        - `"failed"`: the agent could not complete the task.
        - `"blocked"` / `"needs-human"`: the agent cannot proceed without a human decision (handled identically); explain why in `humanReason`.

        Example:

        ```json
        {
          "status": "completed",
          "summary": "Added CSV export for the billing report.",
          "testsRun": ["dotnet test"],
          "testsPassed": true,
          "filesChanged": ["src/Billing/CsvExporter.cs"],
          "risks": [],
          "needsHuman": false,
          "humanReason": null
        }
        ```
        """;

    private static string DescribeFeedback(TaskFeedback? feedback)
    {
        if (feedback is null) return "";
        return $"""
            ## Operator feedback

            A human reviewed this task's work and provided the following instructions. Address this directly —
            it takes priority over any conflicting earlier assumption.

            {feedback.Body}

            """;
    }

    private static string DescribePreviousAttempt(PreviousAttemptSummary? previous)
    {
        if (previous is null) return "";
        var files = previous.ChangedFiles.Count > 0 ? string.Join(", ", previous.ChangedFiles) : "none recorded";
        return $"""
            ## Previous attempt

            The previous attempt did not succeed. Fix the underlying problem instead of repeating the same approach.

            **Agent summary:** {previous.AgentSummary ?? "none recorded"}

            **Validation output:**

            {previous.ValidationOutput ?? "Validation did not run for the previous attempt."}

            **Files changed:** {files} (+{previous.LinesAdded} -{previous.LinesRemoved})

            """;
    }
}
