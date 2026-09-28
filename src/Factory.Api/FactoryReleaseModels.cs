using Factory.Core;

namespace Factory.Api;

public sealed record CreateFactoryReleaseRequest(long RepositoryId, string Name, string ReleaseNumber,
    string? TargetBranch, IReadOnlyList<long>? GitHubIssueIds = null, long? GitHubMilestoneId = null);

public sealed record RetryFactoryReleaseRequest(string? IntegrationBranch = null, string? TargetBranch = null);

public sealed class FactoryReleaseApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class FactoryReleaseService(IFactoryReleaseStore releases, IGitHubStore github)
{
    public Task<IReadOnlyList<FactoryRelease>> ListAsync(CancellationToken cancellationToken) => releases.ListAsync(cancellationToken);
    public Task<FactoryRelease?> GetAsync(Guid id, CancellationToken cancellationToken) => releases.GetAsync(id, cancellationToken);

    public async Task<FactoryRelease> CreateAsync(CreateFactoryReleaseRequest request, CancellationToken cancellationToken)
    {
        var repository = await github.GetRepositoryAsync(request.RepositoryId, cancellationToken);
        if (repository is null)
            throw new FactoryReleaseApiException("The selected repository is not synchronized.", StatusCodes.Status404NotFound);
        if (!repository.IsEnabled)
            throw new FactoryReleaseApiException("Enable the repository before creating a release.", StatusCodes.Status409Conflict);

        var name = request.Name?.Trim() ?? "";
        var number = request.ReleaseNumber?.Trim() ?? "";
        var targetBranch = string.IsNullOrWhiteSpace(request.TargetBranch) ? repository.DefaultBranch : request.TargetBranch.Trim();
        if (name.Length is 0 or > 120 || name.Any(char.IsControl))
            throw new FactoryReleaseApiException("Enter a release name between 1 and 120 characters.", StatusCodes.Status400BadRequest);
        if (number.Length is 0 or > 80 || number.Any(char.IsControl))
            throw new FactoryReleaseApiException("Enter a release number between 1 and 80 characters.", StatusCodes.Status400BadRequest);
        if (targetBranch.Length is 0 or > 240 || targetBranch.Any(char.IsControl))
            throw new FactoryReleaseApiException("Enter a target branch between 1 and 240 characters.", StatusCodes.Status400BadRequest);
        if (request.GitHubMilestoneId is <= 0)
            throw new FactoryReleaseApiException("GitHub milestone identity must be a positive number.", StatusCodes.Status400BadRequest);

        var issueIds = request.GitHubIssueIds ?? [];
        if (issueIds.Count > 250 || issueIds.Any(id => id <= 0))
            throw new FactoryReleaseApiException("Select no more than 250 synchronized issues.", StatusCodes.Status400BadRequest);

        FactoryRelease? created;
        try
        {
            created = await releases.CreateAsync(new FactoryReleaseDraft(repository.Id, name, number, targetBranch,
                request.GitHubMilestoneId), issueIds, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new FactoryReleaseApiException(exception.Message, StatusCodes.Status409Conflict);
        }
        if (created is null)
            throw new FactoryReleaseApiException("This repository already has a release with that number.", StatusCodes.Status409Conflict);
        return created;
    }

    public async Task<FactoryRelease> RetryAsync(Guid id, string? integrationBranch, string? targetBranch, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(integrationBranch) &&
            (integrationBranch.Length > 240 || integrationBranch.Any(char.IsControl)))
            throw new FactoryReleaseApiException("Integration branch names must be 240 characters or fewer.", StatusCodes.Status400BadRequest);
        if (!string.IsNullOrWhiteSpace(targetBranch) &&
            (targetBranch.Length > 240 || targetBranch.Any(char.IsControl)))
            throw new FactoryReleaseApiException("Target branch names must be 240 characters or fewer.", StatusCodes.Status400BadRequest);
        if (!await releases.RetryAsync(id, integrationBranch, targetBranch, cancellationToken))
            throw await ActionConflictAsync(id, "Only a failed release can be retried.", cancellationToken);
        return (await releases.GetAsync(id, cancellationToken))!;
    }

    public async Task<FactoryRelease> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await releases.CancelAsync(id, cancellationToken))
            throw await ActionConflictAsync(id, "Only a pending or failed release can be cancelled.", cancellationToken);
        return (await releases.GetAsync(id, cancellationToken))!;
    }

    public async Task<FactoryRelease> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await releases.ArchiveAsync(id, cancellationToken))
            throw await ActionConflictAsync(id, "Only an active, failed, or cancelled release can be archived.", cancellationToken);
        return (await releases.GetAsync(id, cancellationToken))!;
    }

    private async Task<FactoryReleaseApiException> ActionConflictAsync(Guid id, string message, CancellationToken cancellationToken)
    {
        return await releases.GetAsync(id, cancellationToken) is null
            ? new FactoryReleaseApiException("Release not found.", StatusCodes.Status404NotFound)
            : new FactoryReleaseApiException(message, StatusCodes.Status409Conflict);
    }
}
