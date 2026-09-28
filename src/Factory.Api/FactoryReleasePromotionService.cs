using System.Security.Cryptography;
using System.Text;
using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Npgsql;

namespace Factory.Api;

/// <summary>Checks persisted release membership against the exact GitHub refs, then opens and tracks one
/// integration-branch-to-target pull request. This workflow deliberately never merges or deletes branches.</summary>
public sealed class FactoryReleasePromotionService(NpgsqlDataSource dataSource, IFactoryReleaseStore releases,
    IGitHubStore github, IGitHubClient githubClient, IGitHubPublisher publisher, IClock clock)
{
    private const string NotChecked = "NotChecked";

    public Task<FactoryRelease> CheckAsync(Guid id, CancellationToken cancellationToken) =>
        WithPromotionLockAsync(id, () => CheckLockedAsync(id, cancellationToken), cancellationToken);

    public Task<FactoryRelease> PromoteAsync(Guid id, CancellationToken cancellationToken) =>
        WithPromotionLockAsync(id, () => PromoteLockedAsync(id, cancellationToken), cancellationToken);

    private async Task<FactoryRelease> CheckLockedAsync(Guid id, CancellationToken cancellationToken)
    {
        var release = await RequireReleaseAsync(id, cancellationToken);
        if (release.Promotion?.PullRequestNumber is not null)
            return await RefreshPullRequestAsync(release, release.Promotion, cancellationToken);

        var candidate = await CheckCandidateAsync(release, release.Promotion, cancellationToken);
        await releases.SavePromotionAsync(id, candidate, cancellationToken);
        return await RequireReleaseAsync(id, cancellationToken);
    }

    private async Task<FactoryRelease> PromoteLockedAsync(Guid id, CancellationToken cancellationToken)
    {
        var release = await RequireReleaseAsync(id, cancellationToken);
        if (release.Promotion?.PullRequestNumber is not null)
            return await RefreshPullRequestAsync(release, release.Promotion, cancellationToken);

        var previousCheck = release.Promotion;
        var currentCheck = await CheckCandidateAsync(release, previousCheck, cancellationToken);
        if (previousCheck is null || previousCheck.Status != "Ready" || previousCheck.LastCheckedAt is null)
        {
            await releases.SavePromotionAsync(id, currentCheck, cancellationToken);
            throw new FactoryReleaseApiException("Check release readiness before preparing its pull request.", StatusCodes.Status409Conflict);
        }
        if (currentCheck.Status != "Ready")
        {
            await releases.SavePromotionAsync(id, currentCheck, cancellationToken);
            throw new FactoryReleaseApiException("Release readiness has changed. Review the latest blockers and check again.", StatusCodes.Status409Conflict);
        }
        if (currentCheck.HeadCommit != previousCheck.HeadCommit || currentCheck.TargetCommit != previousCheck.TargetCommit ||
            currentCheck.MembershipHash != previousCheck.MembershipHash)
        {
            var stale = currentCheck with
            {
                Status = "Stale",
                Blockers = currentCheck.Blockers.Append("The integration branch, target branch, or selected issue set changed after the last readiness check. Review and check again.").ToArray()
            };
            await releases.SavePromotionAsync(id, stale, cancellationToken);
            throw new FactoryReleaseApiException("The release changed after its last readiness check. Review the new state before preparing a pull request.", StatusCodes.Status409Conflict);
        }

        var repository = await RequireRepositoryAsync(release, cancellationToken);
        var existing = await publisher.FindExistingReleasePullRequestAsync(repository.Owner, repository.Name,
            release.IntegrationBranch!, cancellationToken);
        if (existing is { Succeeded: false })
        {
            var failed = currentCheck with { Status = "Blocked", Blockers = [.. currentCheck.Blockers, "Could not check for an existing release pull request."], Error = existing.Error };
            await releases.SavePromotionAsync(id, failed, cancellationToken);
            throw new FactoryReleaseApiException(existing.Error ?? "Could not check for an existing release pull request.", StatusCodes.Status502BadGateway);
        }
        if (existing is not null)
        {
            if (!string.Equals(existing.BaseBranch, release.TargetBranch, StringComparison.Ordinal))
            {
                var wrongBase = currentCheck with
                {
                    Status = "Blocked", PullRequestNumber = existing.Number, PullRequestUrl = existing.Url,
                    Blockers = [.. currentCheck.Blockers, $"An existing pull request from this integration branch targets '{existing.BaseBranch}', not '{release.TargetBranch}'."],
                    Error = "An existing pull request has the wrong target branch."
                };
                await releases.SavePromotionAsync(id, wrongBase, cancellationToken);
                throw new FactoryReleaseApiException(wrongBase.Error, StatusCodes.Status409Conflict);
            }

            if (!TryReadMarker(existing.Body, out var marker) || marker.ReleaseId != id ||
                marker.MembershipHash != currentCheck.MembershipHash)
            {
                var unowned = currentCheck with
                {
                    Status = "Blocked", PullRequestNumber = existing.Number, PullRequestUrl = existing.Url,
                    Blockers = [.. currentCheck.Blockers, "An existing pull request uses this branch but does not match this release's frozen issue set."],
                    Error = "The existing pull request cannot be safely attributed to this release."
                };
                await releases.SavePromotionAsync(id, unowned, cancellationToken);
                throw new FactoryReleaseApiException(unowned.Error, StatusCodes.Status409Conflict);
            }

            var recovered = currentCheck with
            {
                PullRequestNumber = existing.Number, PullRequestUrl = existing.Url,
                FrozenHeadCommit = marker.HeadCommit, FrozenTargetCommit = marker.TargetCommit,
                FrozenMembershipHash = marker.MembershipHash,
                MembershipIssueIds = currentCheck.MembershipIssueIds
            };
            await releases.SavePromotionAsync(id, recovered, cancellationToken);
            return await RefreshPullRequestAsync(await RequireReleaseAsync(id, cancellationToken), recovered, cancellationToken);
        }

        // Re-read issue/task/CI state and both refs after the existing-PR lookup, immediately before the GitHub
        // write. The earlier readiness check is the operator-reviewed snapshot; no late change is included silently.
        var finalRelease = await RequireReleaseAsync(id, cancellationToken);
        var finalCheck = await CheckCandidateAsync(finalRelease, previousCheck, cancellationToken);
        if (finalCheck.Status != "Ready" || finalCheck.HeadCommit != previousCheck.HeadCommit ||
            finalCheck.TargetCommit != previousCheck.TargetCommit || finalCheck.MembershipHash != previousCheck.MembershipHash)
        {
            var stale = finalCheck with
            {
                Status = finalCheck.Status == "Ready" ? "Stale" : finalCheck.Status,
                Blockers = finalCheck.Blockers.Append("The integration or target branch or selected issue state changed after the readiness check. Review and check again.").Distinct().ToArray()
            };
            await releases.SavePromotionAsync(id, stale, cancellationToken);
            throw new FactoryReleaseApiException("The integration or target branch changed after the readiness check. Review the latest state before preparing a pull request.", StatusCodes.Status409Conflict);
        }

        var body = BuildPullRequestBody(finalRelease, finalCheck);
        var created = await publisher.CreatePullRequestAsync(repository.Owner, repository.Name, release.IntegrationBranch!,
            release.TargetBranch, $"Release {release.ReleaseNumber}: {release.Name}", body, draft: false, cancellationToken);
        if (!created.Succeeded || created.Number is null || string.IsNullOrWhiteSpace(created.Url))
        {
            // Recover a successful GitHub write if the CLI failed while returning its response.
            existing = await publisher.FindExistingReleasePullRequestAsync(repository.Owner, repository.Name,
                release.IntegrationBranch!, cancellationToken);
            if (existing is null || !existing.Succeeded || existing.Number is null || string.IsNullOrWhiteSpace(existing.Url) ||
                !string.Equals(existing.BaseBranch, release.TargetBranch, StringComparison.Ordinal) ||
                !TryReadMarker(existing.Body, out var marker) || marker.ReleaseId != id || marker.MembershipHash != finalCheck.MembershipHash)
            {
                var failed = finalCheck with { Status = "Blocked", Error = created.Error ?? "GitHub did not return a release pull request." };
                await releases.SavePromotionAsync(id, failed, cancellationToken);
                throw new FactoryReleaseApiException(failed.Error!, StatusCodes.Status502BadGateway);
            }
            created = new PullRequestResult(true, existing.Number, existing.Url, null);
        }

        var opened = finalCheck with
        {
            Status = "PullRequestOpen", PullRequestNumber = created.Number, PullRequestUrl = created.Url,
            FrozenHeadCommit = finalCheck.HeadCommit, FrozenTargetCommit = finalCheck.TargetCommit,
            FrozenMembershipHash = finalCheck.MembershipHash, MembershipIssueIds = finalCheck.MembershipIssueIds,
            MergeabilityStatus = "NotChecked", CiStatus = "Pending", BranchCleanupEligible = false
        };
        await releases.SavePromotionAsync(id, opened, cancellationToken);
        return await RefreshPullRequestAsync(await RequireReleaseAsync(id, cancellationToken), opened, cancellationToken);
    }

    private async Task<FactoryReleasePromotion> CheckCandidateAsync(FactoryRelease release,
        FactoryReleasePromotion? previous, CancellationToken cancellationToken)
    {
        var evaluation = ReleasePromotionReadiness.Evaluate(release);
        var repository = await github.GetRepositoryAsync(release.RepositoryId, cancellationToken);
        var blockers = evaluation.Blockers.ToList();
        if (repository is null)
            blockers.Add("The repository is not synchronized.");

        var refs = repository is null
            ? new BranchRefs(null, null, ["Could not read the repository's branch refs."])
            : await ReadRefsAsync(repository, release, cancellationToken);
        blockers.AddRange(refs.Errors);

        var issueIds = release.Issues.Select(issue => issue.GitHubIssueId).Order().ToArray();
        var membershipHash = HashMembership(issueIds);
        if (previous?.FrozenMembershipHash is { } frozenHash && !string.Equals(frozenHash, membershipHash, StringComparison.Ordinal))
            blockers.Add("The selected issue set changed after the promotion pull request was prepared.");

        return new FactoryReleasePromotion(
            Status: blockers.Count == 0 && evaluation.Ready && refs.Ready ? "Ready" : "Blocked",
            PullRequestNumber: previous?.PullRequestNumber, PullRequestUrl: previous?.PullRequestUrl,
            HeadCommit: refs.Head, TargetCommit: refs.Target,
            FrozenHeadCommit: previous?.FrozenHeadCommit, FrozenTargetCommit: previous?.FrozenTargetCommit,
            MembershipHash: membershipHash, FrozenMembershipHash: previous?.FrozenMembershipHash,
            MembershipIssueIds: previous?.MembershipIssueIds ?? [], CiStatus: evaluation.CiStatus,
            MergeabilityStatus: previous?.MergeabilityStatus ?? NotChecked, LastCheckedAt: clock.UtcNow,
            RemainingIssues: evaluation.RemainingIssues, Blockers: blockers.Distinct().ToArray(),
            Conflicts: previous?.Conflicts ?? [], BranchCleanupEligible: false,
            Error: refs.Errors.Count == 0 ? null : string.Join(" ", refs.Errors));
    }

    private async Task<FactoryRelease> RefreshPullRequestAsync(FactoryRelease release, FactoryReleasePromotion previous,
        CancellationToken cancellationToken)
    {
        var repository = await RequireRepositoryAsync(release, cancellationToken);
        var refs = await ReadRefsAsync(repository, release, cancellationToken);
        var evaluation = ReleasePromotionReadiness.Evaluate(release);
        var blockers = evaluation.Blockers.Concat(refs.Errors).ToList();
        var conflicts = new List<string>();
        var mergeability = await githubClient.GetPullRequestMergeabilityAsync(repository.Owner, repository.Name,
            previous.PullRequestNumber!.Value, cancellationToken);
        var checks = await githubClient.GetPullRequestChecksAsync(repository.Owner, repository.Name,
            previous.PullRequestNumber.Value, cancellationToken);
        var state = await githubClient.GetPullRequestStateAsync(repository.Owner, repository.Name,
            previous.PullRequestNumber.Value, cancellationToken);
        var ciStatus = PullRequestCiStatus.Overall(checks);
        var stale = false;
        var membershipHash = HashMembership(release.Issues.Select(issue => issue.GitHubIssueId).Order().ToArray());

        if (previous.FrozenMembershipHash is not null && previous.FrozenMembershipHash != membershipHash)
        {
            stale = true;
            blockers.Add("The selected issue set changed after the promotion pull request was prepared.");
        }

        if (!mergeability.Succeeded)
            blockers.Add(mergeability.Error ?? "GitHub could not read pull request mergeability.");
        else
        {
            if (!string.Equals(mergeability.HeadBranch, release.IntegrationBranch, StringComparison.Ordinal) ||
                !string.Equals(mergeability.BaseBranch, release.TargetBranch, StringComparison.Ordinal))
                blockers.Add("The pull request head or target branch does not match this release.");
            if (refs.Head is not null && mergeability.HeadSha != refs.Head ||
                refs.Target is not null && mergeability.BaseSha != refs.Target)
            {
                stale = true;
                blockers.Add("The pull request head or target changed while its status was being checked.");
            }
            if (mergeability.Status == "Conflict")
            {
                conflicts.Add("GitHub reports merge conflicts between the integration and target branches.");
                blockers.AddRange(conflicts);
            }
            if (mergeability.Status == "Unavailable") blockers.Add(mergeability.Error ?? "Mergeability is unavailable.");
        }

        if (previous.FrozenHeadCommit is null || previous.FrozenTargetCommit is null || previous.FrozenMembershipHash is null)
        {
            stale = true;
            blockers.Add("The pull request has no persisted review snapshot. It must be checked by an operator.");
        }
        if (state is null)
            blockers.Add("GitHub could not read whether the pull request is open or merged.");

        var merged = state?.Merged == true;
        var closed = state?.Closed == true;
        if (merged)
        {
            if (refs.Head is not null && previous.FrozenHeadCommit != refs.Head)
            {
                stale = true;
                blockers.Add("The integration branch has commits beyond the reviewed pull request head.");
            }
        }
        else
        {
            if (previous.FrozenHeadCommit != refs.Head || previous.FrozenTargetCommit != refs.Target)
            {
                stale = true;
                blockers.Add("The integration or target branch changed after the pull request review snapshot was frozen.");
            }
        }

        if (!checks.Succeeded)
            blockers.Add(checks.Error ?? "GitHub could not read release pull request CI.");
        else if (mergeability.Succeeded && checks.HeadSha != mergeability.HeadSha)
        {
            stale = true;
            ciStatus = "Stale";
            blockers.Add("The CI result belongs to a different pull request head.");
        }
        if (!merged && ciStatus != PullRequestCiStatus.Success)
            blockers.Add($"Release pull request CI is {ciStatus.ToLowerInvariant()}.");

        var status = merged ? stale ? "Stale" : "Merged"
            : closed ? "Closed"
            : stale ? "Stale"
            : conflicts.Count > 0 || blockers.Count > 0 ? "Blocked"
            : "PullRequestOpen";
        var promotion = previous with
        {
            Status = status, HeadCommit = refs.Head, TargetCommit = refs.Target,
            MembershipHash = membershipHash, CiStatus = ciStatus, MergeabilityStatus = mergeability.Status,
            LastCheckedAt = clock.UtcNow, RemainingIssues = evaluation.RemainingIssues,
            Blockers = blockers.Distinct().ToArray(), Conflicts = conflicts,
            BranchCleanupEligible = merged && !stale && (refs.Head is null || refs.Head == previous.FrozenHeadCommit),
            Error = refs.Errors.Count > 0 ? string.Join(" ", refs.Errors) : mergeability.Error ?? checks.Error
        };
        await releases.SavePromotionAsync(release.Id, promotion, cancellationToken);
        return await RequireReleaseAsync(release.Id, cancellationToken);
    }

    private async Task<BranchRefs> ReadRefsAsync(GitHubRepository repository, FactoryRelease release,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        string? head = null;
        string? target = null;
        if (string.IsNullOrWhiteSpace(release.IntegrationBranch))
            errors.Add("The integration branch has not been prepared.");
        else
        {
            var result = await githubClient.GetBranchCommitAsync(repository.Owner, repository.Name, release.IntegrationBranch, cancellationToken);
            if (result.Succeeded) head = result.Commit;
            else errors.Add($"Could not read integration branch '{release.IntegrationBranch}': {result.Error ?? "unknown GitHub error"}");
        }

        if (string.IsNullOrWhiteSpace(release.TargetBranch))
            errors.Add("The target branch is not configured.");
        else
        {
            var result = await githubClient.GetBranchCommitAsync(repository.Owner, repository.Name, release.TargetBranch, cancellationToken);
            if (result.Succeeded) target = result.Commit;
            else errors.Add($"Could not read target branch '{release.TargetBranch}': {result.Error ?? "unknown GitHub error"}");
        }
        return new BranchRefs(head, target, errors);
    }

    private async Task<FactoryRelease> RequireReleaseAsync(Guid id, CancellationToken cancellationToken) =>
        await releases.GetAsync(id, cancellationToken)
        ?? throw new FactoryReleaseApiException("Integration release not found.", StatusCodes.Status404NotFound);

    private async Task<GitHubRepository> RequireRepositoryAsync(FactoryRelease release, CancellationToken cancellationToken) =>
        await github.GetRepositoryAsync(release.RepositoryId, cancellationToken)
        ?? throw new FactoryReleaseApiException("The release repository is no longer synchronized.", StatusCodes.Status404NotFound);

    private async Task<T> WithPromotionLockAsync<T>(Guid id, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string key = "factory-release-promotion:";
        var locked = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(hashtextextended(@key, 198))", new { key = key + id.ToString("N") }, cancellationToken: cancellationToken));
        if (!locked)
            throw new FactoryReleaseApiException("Another promotion check or request is already running for this release. Refresh shortly.", StatusCodes.Status409Conflict);
        try { return await action(); }
        finally
        {
            await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT pg_advisory_unlock(hashtextextended(@key, 198))", new { key = key + id.ToString("N") }, cancellationToken: CancellationToken.None));
        }
    }

    private static string HashMembership(IReadOnlyList<long> issueIds)
    {
        var value = string.Join(',', issueIds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string BuildPullRequestBody(FactoryRelease release, FactoryReleasePromotion promotion)
    {
        var lines = new List<string>
        {
            $"Promotes integration release **{release.ReleaseNumber} · {release.Name}** from `{release.IntegrationBranch}` to `{release.TargetBranch}`.",
            "",
            "The issue set and both branch heads were checked by Factory immediately before this pull request was created.",
            "Factory will not merge this pull request. A human must review and merge it.",
            "",
            "Included issues:"
        };
        lines.AddRange(release.Issues.Select(issue => $"- {release.Repository}#{issue.IssueNumber} — {issue.Title}"));
        lines.Add("");
        lines.Add($"<!-- factory-release-promotion release={release.Id:N} membership={promotion.MembershipHash} head={promotion.HeadCommit} target={promotion.TargetCommit} -->");
        return string.Join('\n', lines);
    }

    private static bool TryReadMarker(string? body, out PromotionMarker marker)
    {
        marker = default;
        const string prefix = "<!-- factory-release-promotion ";
        var line = body?.Split('\n').FirstOrDefault(value => value.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        if (line is null) return false;
        var start = line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = line.IndexOf(" -->", start, StringComparison.Ordinal);
        if (end < 0) return false;
        var values = line[start..end].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (!values.TryGetValue("release", out var release) || !Guid.TryParseExact(release, "N", out var releaseId) ||
            !values.TryGetValue("membership", out var membership) || !values.TryGetValue("head", out var head) ||
            !values.TryGetValue("target", out var target)) return false;
        marker = new PromotionMarker(releaseId, membership, head, target);
        return true;
    }

    private sealed record BranchRefs(string? Head, string? Target, IReadOnlyList<string> Errors)
    {
        public bool Ready => Head is not null && Target is not null && Errors.Count == 0;
    }

    private readonly record struct PromotionMarker(Guid ReleaseId, string MembershipHash, string HeadCommit, string TargetCommit);
}
