using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>
/// Executes one publication request: pushes the task's own factory branch and opens a draft pull request.
/// Never pushes anything but that branch, never targets the repository's base branch directly, and never merges.
/// Every step is idempotent under retry, so a publication reclaimed after a crash (see
/// <see cref="ITaskStore.ClaimNextPublicationAsync"/>) can safely re-run this from the top.
/// </summary>
public sealed class PublicationExecutor(ITaskStore tasks, IGitHubPublisher publisher, IWorktreeInspector inspector, ILogger<PublicationExecutor> logger)
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

            // Independently re-confirms what PreparePublicationStep already checked once: the worktree can have
            // moved (a later attempt, a manual edit) between validation and this publication actually running,
            // especially after a crash-and-reclaim. A mismatch here means the current worktree is no longer what
            // was validated, so it must never be silently published.
            // The repository cache tracks origin explicitly (see GitRepositoryCache) and never creates a local
            // branch matching the base branch name, so the git ref to resolve is origin/<base>, not the bare name.
            var summary = await inspector.SummarizeAsync(request.WorktreePath, $"origin/{request.BaseBranch}", cancellationToken);
            if (!summary.IsClean || !string.Equals(summary.CurrentBranch, request.BranchName, StringComparison.Ordinal))
            {
                await FailAsync(request, $"Worktree at '{request.WorktreePath}' is not in the validated state expected for branch '{request.BranchName}'.", cancellationToken);
                return;
            }
            if (request.ValidatedHeadCommit is { } validatedHeadCommit && !string.Equals(summary.HeadCommit, validatedHeadCommit, StringComparison.Ordinal))
            {
                await FailAsync(request, $"Worktree HEAD '{summary.HeadCommit}' no longer matches the validated commit '{validatedHeadCommit}'; refusing to publish unvalidated changes.", cancellationToken);
                return;
            }

            var push = await publisher.PushAsync(request.WorktreePath, request.BranchName, cancellationToken);
            if (!push.Succeeded)
            {
                await FailAsync(request, push.Error ?? "git push failed.", cancellationToken);
                return;
            }

            // Reconciles a prior interrupted attempt: GitHub may already have the pull request from a crash after
            // a prior `gh pr create` succeeded but before that success was recorded locally. Checking first keeps
            // a reclaimed publication idempotent instead of failing on, or duplicating, an existing pull request.
            var existing = await publisher.FindExistingPullRequestAsync(request.RepositoryOwner, request.RepositoryName, request.BranchName, cancellationToken);
            if (existing is { Succeeded: false })
            {
                await FailAsync(request, existing.Error ?? "Failed to check for an existing pull request.", cancellationToken);
                return;
            }

            PullRequestResult pullRequest;
            if (existing is not null)
            {
                pullRequest = existing;
                logger.LogInformation("Publication {PublicationId} found existing pull request {PullRequestUrl} for branch {BranchName}; recovering instead of creating a duplicate.",
                    request.Id, existing.Url, request.BranchName);
            }
            else
            {
                var (title, body) = DescribePullRequest(request);
                pullRequest = await publisher.CreatePullRequestAsync(request.RepositoryOwner, request.RepositoryName,
                    request.BranchName, request.BaseBranch, title, body, cancellationToken);
                if (!pullRequest.Succeeded)
                {
                    await FailAsync(request, pullRequest.Error ?? "gh pr create failed.", cancellationToken);
                    return;
                }
            }

            await tasks.CompletePublicationAsync(request.Id, "PullRequestCreated", pullRequest.Number, pullRequest.Url, null, cancellationToken);

            try
            {
                await tasks.TransitionAsync(request.TaskId, FactoryTaskStatus.ReadyForPublish, FactoryTaskStatus.Published, null, cancellationToken);
                logger.LogInformation("Published task {TaskId} as pull request {PullRequestUrl}", request.TaskId, pullRequest.Url);
            }
            catch (InvalidOperationException ex)
            {
                // The task left ReadyForPublish through some other authoritative action (most likely a human
                // cancelling it) while this publication was in flight. The pull request is still correctly
                // recorded above either way; that resting-state change stays authoritative, so this publication
                // must not be reported as failed, and the task's own status must not be overridden back.
                logger.LogWarning(ex, "Task {TaskId} was no longer ReadyForPublish after pull request {PullRequestUrl} was recorded; leaving its status unchanged.",
                    request.TaskId, pullRequest.Url);
            }
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
