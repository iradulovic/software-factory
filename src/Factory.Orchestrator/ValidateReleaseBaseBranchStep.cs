using Factory.Core;
using Microsoft.Extensions.Logging;

namespace Factory.Orchestrator;

/// <summary>Verifies the task's captured release assignment and confirms its integration branch still exists
/// in the synchronized repository before any coding agent is dispatched.</summary>
public sealed class ValidateReleaseBaseBranchStep(
    ITaskStore tasks,
    IFactoryReleaseStore releases,
    IRepositoryCache repositories,
    IProcessRunner processes,
    ILogger<ValidateReleaseBaseBranchStep> logger) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        if (context.Task.ReleaseId is not { } releaseId) return PipelineStepResult.Ok;

        var stepId = await tasks.StartStepAsync(context.RunId, "ValidateReleaseBaseBranch", 1, cancellationToken);
        var release = await releases.GetAsync(releaseId, cancellationToken);
        var issueIsMember = context.Task.GitHubIssueId is { } issueId &&
            release?.Issues.Any(issue => issue.GitHubIssueId == issueId) == true;
        if (release is null || release.RepositoryId != context.Task.RepositoryId ||
            context.Repository?.Id != context.Task.RepositoryId || !issueIsMember ||
            release.IntegrationBranch != context.Task.BaseBranch ||
            release.Status is not (FactoryReleaseStatus.Active or FactoryReleaseStatus.Archived))
        {
            var reason = $"The task's captured release assignment could not be verified for repository {context.Task.RepositoryId} and base branch '{context.Task.BaseBranch}'. The task was not retargeted.";
            return await NeedsHumanAsync(stepId, reason, cancellationToken);
        }

        string cachePath;
        try
        {
            cachePath = await repositories.PrepareAsync(context.Repository!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var reason = $"Could not verify captured release base branch '{context.Task.BaseBranch}' in {release.Repository}: {exception.Message}. Restore the branch or repository access, then continue this task.";
            return await NeedsHumanAsync(stepId, reason, cancellationToken);
        }

        var branch = await processes.RunAsync(new ProcessRequest("git",
            ["rev-parse", "--verify", "--quiet", "--end-of-options", $"refs/remotes/origin/{context.Task.BaseBranch}^{{commit}}"],
            cachePath, Timeout: TimeSpan.FromMinutes(1)), cancellationToken);
        if (!branch.Succeeded || string.IsNullOrWhiteSpace(branch.StandardOutput))
        {
            var reason = $"Captured release base branch '{context.Task.BaseBranch}' no longer exists in {release.Repository}. The task remains assigned to release {release.ReleaseNumber}; restore the branch before retrying. It will not fall back to the repository default branch.";
            logger.LogWarning("Task {TaskId} release {ReleaseId} cannot be dispatched because base branch {BaseBranch} is unavailable in repository {RepositoryId}",
                context.Task.Id, releaseId, context.Task.BaseBranch, context.Task.RepositoryId);
            return await NeedsHumanAsync(stepId, reason, cancellationToken);
        }

        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null,
            $"Verified {release.Repository}:{context.Task.BaseBranch} at {branch.StandardOutput.Trim()}.", cancellationToken);
        return PipelineStepResult.Ok;
    }

    private async Task<PipelineStepResult> NeedsHumanAsync(Guid stepId, string reason, CancellationToken cancellationToken)
    {
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
        return PipelineStepResult.NeedsHuman(reason);
    }
}
