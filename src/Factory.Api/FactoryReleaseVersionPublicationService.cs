using System.Security.Cryptography;
using System.Text;
using Factory.Core;

namespace Factory.Api;

/// <summary>Publishes a repository-local version only after the promotion flow has verified the merged PR and its
/// frozen evidence. Remote writes are idempotent, and the tag is durably recorded before Release creation.</summary>
public sealed class FactoryReleaseVersionPublicationService(IFactoryReleaseStore releases, IGitHubStore github,
    IRepositoryVersionPublisher publisher, IFactoryReleaseVersionStore versions, IClock clock)
{
    public async Task<FactoryRelease> PublishMergedAsync(FactoryRelease release,
        FactoryReleasePromotion promotion, PullRequestState? pullRequest, string? integrationHead,
        string? targetHead, bool promotionEvidenceValid, string? blocker, CancellationToken cancellationToken)
    {
        var existing = promotion.VersionPublication;
        if (existing?.Status is "Published" or "Conflict")
            return release;

        var repository = await github.GetRepositoryAsync(release.RepositoryId, cancellationToken);
        var repositoryName = repository is null ? release.Repository : $"{repository.Owner}/{repository.Name}";
        var version = release.ReleaseNumber;
        var validVersion = SemanticReleaseVersion.TryParse(version, out _);
        var tagName = validVersion ? $"v{version}" : null;
        var now = clock.UtcNow;
        var publication = existing ?? new FactoryReleaseVersionPublication("NotStarted", release.RepositoryId,
            repositoryName, version, tagName, null, null, null, null, null, null, null, null, 0, null);
        publication = publication with
        {
            RepositoryId = release.RepositoryId,
            Repository = repositoryName,
            PlannedVersion = version,
            TagName = tagName,
            TargetBranchCommit = pullRequest?.MergeCommit ?? publication.TargetBranchCommit
        };

        var membershipHash = HashMembership(release.Issues.Select(issue => issue.GitHubIssueId).Order().ToArray());
        var evidenceError = !promotionEvidenceValid ? blocker ?? "The promotion evidence no longer matches the reviewed release."
            : promotion.Status != "Merged" || pullRequest?.Merged != true ? "The promotion pull request is not confirmed as merged."
            : string.IsNullOrWhiteSpace(promotion.FrozenHeadCommit) || pullRequest.HeadCommit != promotion.FrozenHeadCommit ||
              integrationHead is not null && integrationHead != promotion.FrozenHeadCommit
                ? "The merged pull request head no longer matches the reviewed integration branch head."
            : string.IsNullOrWhiteSpace(promotion.FrozenMembershipHash) || promotion.FrozenMembershipHash != membershipHash
                ? "The selected issue set no longer matches the frozen release selection."
            : string.IsNullOrWhiteSpace(release.TargetBranch) || pullRequest.BaseBranch != release.TargetBranch
                ? "The merged pull request target no longer matches the intended target branch."
            : string.IsNullOrWhiteSpace(pullRequest.MergeCommit) || string.IsNullOrWhiteSpace(targetHead)
                ? "GitHub did not provide a verifiable target-branch commit for the merged pull request."
            : !validVersion ? $"Planned version '{version}' is not a valid MAJOR.MINOR.PATCH version."
            : repository is null ? "The release repository is no longer synchronized."
            : null;

        if (evidenceError is not null)
        {
            if (publication.Status != "Blocked" || publication.LastError != evidenceError ||
                publication.TargetBranchCommit != pullRequest?.MergeCommit)
            {
                publication = publication with
                {
                    Status = "Blocked", TargetBranchCommit = pullRequest?.MergeCommit,
                    LastError = evidenceError, CompletedAt = null
                };
                await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
            }
            return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
        }

        var verifiedCommit = await publisher.VerifyCommitOnBranchAsync(repository!, release.TargetBranch,
            pullRequest!.MergeCommit!, cancellationToken);
        if (!verifiedCommit.Succeeded)
        {
            publication = RetryableFailure(publication, verifiedCommit.Error ?? "GitHub could not verify the merged commit on the target branch.", now);
            await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
            return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
        }
        if (!verifiedCommit.CommitIsOnBranch)
        {
            var reason = verifiedCommit.Error ?? $"The merged commit is not present on target branch '{release.TargetBranch}'.";
            publication = publication with { Status = "Blocked", TargetBranchCommit = pullRequest.MergeCommit, LastError = reason };
            await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
            return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
        }
        var verifiedFrozenTarget = await publisher.VerifyCommitOnBranchAsync(repository!, release.TargetBranch,
            promotion.FrozenTargetCommit!, cancellationToken);
        if (!verifiedFrozenTarget.Succeeded)
        {
            publication = RetryableFailure(publication, verifiedFrozenTarget.Error ?? "GitHub could not verify the reviewed target commit.", now);
            await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
            return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
        }
        if (!verifiedFrozenTarget.CommitIsOnBranch)
        {
            var reason = verifiedFrozenTarget.Error ??
                $"The reviewed target commit is no longer present on target branch '{release.TargetBranch}'.";
            publication = publication with { Status = "Blocked", TargetBranchCommit = pullRequest.MergeCommit, LastError = reason };
            await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
            return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
        }

        publication = publication with
        {
            Status = "Publishing", TargetBranchCommit = pullRequest.MergeCommit, LastAttemptAt = now,
            StartedAt = publication.StartedAt ?? now, CompletedAt = null, AttemptCount = checked(publication.AttemptCount + 1),
            LastError = null
        };
        await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);

        try
        {
            var tag = await publisher.EnsureTagAsync(repository!, tagName!, pullRequest.MergeCommit!, cancellationToken);
            if (tag.Status == "Conflict")
            {
                publication = publication with { Status = "Conflict", LastError = tag.Error, CompletedAt = clock.UtcNow };
                await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
                return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
            }
            if (tag.Status == "Failed")
            {
                publication = publication with { Status = "Failed", LastError = tag.Error, CompletedAt = clock.UtcNow };
                await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
                return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
            }

            publication = publication with
            {
                Status = "TagCreated", TargetBranchCommit = tag.Commit ?? pullRequest.MergeCommit,
                TagRecordedAt = publication.TagRecordedAt ?? clock.UtcNow, LastError = null, CompletedAt = null
            };
            await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);

            var createdRelease = await publisher.EnsureReleaseAsync(repository!, tagName!,
                $"v{version} — {release.Name}", BuildReleaseSummary(release), cancellationToken);
            if (createdRelease.Status == "Conflict")
            {
                publication = publication with { Status = "Conflict", LastError = createdRelease.Error, CompletedAt = clock.UtcNow };
            }
            else if (createdRelease.Status == "Failed")
            {
                // The tag is already durable. Keep this explicit partial-success state so retry only needs to
                // reconcile the tag and complete GitHub Release creation.
                publication = publication with { Status = "TagCreated", LastError = createdRelease.Error };
            }
            else
            {
                await versions.RecordFactoryPublishedVersionAsync(release.RepositoryId, version, tagName!, cancellationToken);
                publication = publication with
                {
                    Status = "Published", GitHubReleaseId = createdRelease.ReleaseId,
                    GitHubReleaseUrl = createdRelease.Url, PublishedAt = createdRelease.PublishedAt ?? clock.UtcNow,
                    LastError = null, CompletedAt = clock.UtcNow
                };
            }
            await releases.SaveVersionPublicationAsync(release.Id, publication, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            publication = publication with { Status = publication.Status == "TagCreated" ? "TagCreated" : "Failed",
                LastError = exception.Message, CompletedAt = publication.Status == "TagCreated" ? null : clock.UtcNow };
            await releases.SaveVersionPublicationAsync(release.Id, publication, CancellationToken.None);
        }

        return await CurrentAsync(release, promotion with { VersionPublication = publication }, cancellationToken);
    }

    private async Task<FactoryRelease> CurrentAsync(FactoryRelease fallback, FactoryReleasePromotion promotion,
        CancellationToken cancellationToken) =>
        await releases.GetAsync(fallback.Id, cancellationToken) ?? fallback with { Promotion = promotion };

    private static string BuildReleaseSummary(FactoryRelease release)
    {
        var lines = new List<string>
        {
            $"Factory release **{release.ReleaseNumber} — {release.Name}** for {release.Repository}.",
            "",
            $"Promoted to `{release.TargetBranch}` from `{release.IntegrationBranch}`.",
            "",
            "## Included issues and changes"
        };
        lines.AddRange(release.Issues.OrderBy(issue => issue.IssueNumber)
            .Select(issue => $"- #{issue.IssueNumber} — {issue.Title}"));
        return string.Join('\n', lines);
    }

    private static string HashMembership(IReadOnlyList<long> issueIds) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', issueIds)))).ToLowerInvariant();

    private static FactoryReleaseVersionPublication RetryableFailure(FactoryReleaseVersionPublication publication,
        string error, DateTimeOffset now) => publication with
    {
        Status = "Failed", StartedAt = publication.StartedAt ?? now, LastAttemptAt = now,
        AttemptCount = checked(publication.AttemptCount + 1), LastError = error, CompletedAt = now
    };
}
