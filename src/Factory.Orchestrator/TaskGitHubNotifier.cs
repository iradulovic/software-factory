using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Factory.Orchestrator;

/// <summary>
/// Posts a concise issue comment and sets the matching <c>factory:*</c> state label whenever a task starts,
/// completes validation, fails, or needs a human, so people who work in GitHub see what the factory did without
/// opening the dashboard. Every write attempt is recorded through <see cref="IGitHubPublisher"/> and persisted as
/// operational state; a write failure is logged and recorded but never fails the task itself, and a task with no
/// linked GitHub issue is silently skipped.
/// </summary>
public sealed class TaskGitHubNotifier(ITaskStore tasks, IGitHubPublisher publisher, IOptions<FactoryOptions> options, ILogger<TaskGitHubNotifier> logger)
{
    public Task NotifyStartedAsync(PipelineContext context, CancellationToken cancellationToken) =>
        NotifyAsync(context, "factory:in-progress", $"Software Factory started working on this issue.\n\n{DashboardLink(context)}", cancellationToken);

    public Task NotifyReadyForPublishAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var summary = context.ChangeSummary;
        var diff = summary is null ? "" : $"\n\n**Changes:** {summary.FilesChanged.Count} file(s), +{summary.LinesAdded} -{summary.LinesRemoved}.";
        var agentSummary = context.AgentResult?.Summary is { Length: > 0 } text ? $"\n\n{text}" : "";
        var body = $"Validation passed; this task is ready for review.{agentSummary}{diff}\n\n{DashboardLink(context)}";
        return NotifyAsync(context, "factory:ready-for-review", body, cancellationToken);
    }

    public Task NotifyFailedAsync(PipelineContext context, string reason, CancellationToken cancellationToken) =>
        NotifyAsync(context, "factory:failed", $"Task failed: {reason}\n\n{DashboardLink(context)}", cancellationToken);

    public Task NotifyNeedsHumanAsync(PipelineContext context, string reason, CancellationToken cancellationToken) =>
        NotifyAsync(context, "factory:needs-human", $"This task needs human input: {reason}\n\n{DashboardLink(context)}", cancellationToken);

    private async Task NotifyAsync(PipelineContext context, string label, string comment, CancellationToken cancellationToken)
    {
        if (context.Repository is not { } repository || context.Issue is not { } issue) return;

        var commentResult = await TryAsync(() => publisher.CommentOnIssueAsync(repository.Owner, repository.Name, issue.IssueNumber, comment, cancellationToken));
        await tasks.RecordGitHubWriteAsync(context.Task.Id, "comment", Truncate(comment), commentResult.Succeeded, commentResult.Error, cancellationToken);

        var labelResult = await TryAsync(() => publisher.SetStateLabelAsync(repository.Owner, repository.Name, issue.IssueNumber, label, cancellationToken));
        await tasks.RecordGitHubWriteAsync(context.Task.Id, "label", label, labelResult.Succeeded, labelResult.Error, cancellationToken);
    }

    private async Task<GitHubWriteResult> TryAsync(Func<Task<GitHubWriteResult>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "GitHub write-back failed");
            return new GitHubWriteResult(false, ex.Message);
        }
    }

    private string DashboardLink(PipelineContext context) => $"[View in Software Factory]({options.Value.DashboardBaseUrl.TrimEnd('/')}/tasks/{context.Task.Id})";

    private static string Truncate(string value) => value.Length <= 2_000 ? value : value[..2_000];
}
