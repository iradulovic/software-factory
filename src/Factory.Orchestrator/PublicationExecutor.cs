using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Executes one publication request: pushes the task's own factory branch and opens a draft pull request.
/// Never pushes anything but that branch, never targets the repository's base branch directly, and never merges.
/// </summary>
public sealed class PublicationExecutor(ITaskStore tasks, IGitHubPublisher publisher, ILogger<PublicationExecutor> logger)
{
    public async Task ExecuteAsync(PublicationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!request.BranchName.StartsWith("factory/", StringComparison.Ordinal) ||
                string.Equals(request.BranchName, request.BaseBranch, StringComparison.Ordinal))
            {
                await FailAsync(request, $"Refusing to publish unexpected branch '{request.BranchName}'.", cancellationToken);
                return;
            }

            var push = await publisher.PushAsync(request.WorktreePath, request.BranchName, cancellationToken);
            if (!push.Succeeded)
            {
                await FailAsync(request, push.Error ?? "git push failed.", cancellationToken);
                return;
            }

            var (title, body) = DescribePullRequest(request);
            var pullRequest = await publisher.CreatePullRequestAsync(request.RepositoryOwner, request.RepositoryName,
                request.BranchName, request.BaseBranch, title, body, cancellationToken);
            if (!pullRequest.Succeeded)
            {
                await FailAsync(request, pullRequest.Error ?? "gh pr create failed.", cancellationToken);
                return;
            }

            await tasks.CompletePublicationAsync(request.Id, "PullRequestCreated", pullRequest.Number, pullRequest.Url, null, cancellationToken);
            await tasks.TransitionAsync(request.TaskId, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Completed, null, cancellationToken);
            logger.LogInformation("Published task {TaskId} as pull request {PullRequestUrl}", request.TaskId, pullRequest.Url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Publication {PublicationId} for task {TaskId} failed", request.Id, request.TaskId);
            await tasks.CompletePublicationAsync(request.Id, "Failed", null, null, ex.Message, cancellationToken);
        }
    }

    private static (string Title, string Body) DescribePullRequest(PublicationRequest request)
    {
        var title = request.IssueNumber is { } issueNumber ? $"{request.TaskTitle} (#{issueNumber})" : request.TaskTitle;
        var body = request.IssueNumber is { } number
            ? $"Closes #{number}.\n\nPrepared by Software Factory. Human review is required before merge."
            : "Prepared by Software Factory. Human review is required before merge.";
        return (title, body);
    }

    private async Task FailAsync(PublicationRequest request, string error, CancellationToken cancellationToken) =>
        await tasks.CompletePublicationAsync(request.Id, "Failed", null, null, error, cancellationToken);
}
