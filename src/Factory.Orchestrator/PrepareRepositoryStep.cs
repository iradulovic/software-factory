using Factory.Core;

namespace Factory.Orchestrator;

/// <summary>Loads the repository and, if any, the GitHub issue backing the task.</summary>
public sealed class PrepareRepositoryStep(ITaskStore tasks, IGitHubStore github) : IPipelineStep
{
    public async Task<PipelineStepResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var stepId = await tasks.StartStepAsync(context.RunId, "PrepareRepository", 1, cancellationToken);
        var repository = await github.GetRepositoryAsync(context.Task.RepositoryId, cancellationToken);
        if (repository is null)
        {
            const string reason = "Repository not found.";
            await tasks.CompleteStepAsync(stepId, ExecutionStatus.Failed, reason, null, cancellationToken);
            return PipelineStepResult.Failed(reason);
        }

        context.Repository = repository;
        context.Issue = context.Task.GitHubIssueId is { } issueId ? await github.GetIssueAsync(issueId, cancellationToken) : null;
        await tasks.CompleteStepAsync(stepId, ExecutionStatus.Succeeded, null, $"{repository.Owner}/{repository.Name}", cancellationToken);
        return PipelineStepResult.Ok;
    }
}
