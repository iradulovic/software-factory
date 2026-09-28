using Factory.Core;

namespace Factory.Api;

public sealed record CreateFactoryReleaseRequest(long RepositoryId, string Name, string ReleaseNumber,
    string? TargetBranch, IReadOnlyList<long>? GitHubIssueIds = null, long? GitHubMilestoneId = null,
    string? VersionReason = null, string? VersionOverrideReason = null);

public sealed record ReconcileRepositoryReleaseVersionsRequest(IReadOnlyList<string>? PublishedVersions, string? Reason);

public sealed record ReleaseVersionSuggestion(string Reason, string Label, string Version);

public sealed record ObservedReleaseVersion(string Version, bool GitTagObserved, bool GitHubReleaseObserved,
    DateTimeOffset? PublishedAt, long? GitHubReleaseId = null, string? GitHubReleaseUrl = null);

public sealed record RepositoryReleaseVersionPlan(long RepositoryId, string VersionFormat, string TagPrefix,
    string BreakingChangeDefinition, string HistoryStatus, string? HistoryMessage, string? LatestPublishedVersion,
    bool RequiresInitialVersion, IReadOnlyList<ReleaseVersionSuggestion> Suggestions,
    IReadOnlyList<ObservedReleaseVersion> ObservedVersions, IReadOnlyList<string> ConfirmedVersions,
    IReadOnlyList<string> ExistingPlannedVersions, IReadOnlyList<string> LegacyPlannedVersions,
    IReadOnlyList<string> UnrecognizedVersionTags, IReadOnlyList<string> UnrecognizedReleaseTags,
    IReadOnlyList<string> MissingReleaseTags);

public sealed record RetryFactoryReleaseRequest(string? IntegrationBranch = null, string? TargetBranch = null);

public sealed class FactoryReleaseApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class FactoryReleaseService(IFactoryReleaseStore releases, IFactoryReleaseVersionStore versions, IGitHubStore github,
    IRepositoryReleaseVersionHistoryReader versionHistory)
{
    private const int MaxVersionReasonLength = 500;

    public Task<IReadOnlyList<FactoryRelease>> ListAsync(CancellationToken cancellationToken) => releases.ListAsync(cancellationToken);
    public Task<FactoryRelease?> GetAsync(Guid id, CancellationToken cancellationToken) => releases.GetAsync(id, cancellationToken);

    public async Task<RepositoryReleaseVersionPlan> GetVersionPlanAsync(long repositoryId, CancellationToken cancellationToken)
    {
        var repository = await github.GetRepositoryAsync(repositoryId, cancellationToken);
        if (repository is null)
            throw new FactoryReleaseApiException("The selected repository is not synchronized.", StatusCodes.Status404NotFound);

        var state = await versions.GetVersionStateAsync(repositoryId, cancellationToken);
        var history = await ReadHistoryAsync(repository, cancellationToken);
        return BuildVersionPlan(repositoryId, state, history);
    }

    public async Task<RepositoryReleaseVersionPlan> ReconcileVersionHistoryAsync(long repositoryId,
        ReconcileRepositoryReleaseVersionsRequest request, CancellationToken cancellationToken)
    {
        var repository = await github.GetRepositoryAsync(repositoryId, cancellationToken);
        if (repository is null)
            throw new FactoryReleaseApiException("The selected repository is not synchronized.", StatusCodes.Status404NotFound);
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length < 10 || reason.Length > MaxVersionReasonLength || reason.Any(char.IsControl))
            throw new FactoryReleaseApiException("Explain the version history decision in 10 to 500 characters.", StatusCodes.Status400BadRequest);
        var acceptedVersions = request.PublishedVersions ?? [];
        if (acceptedVersions.Count > 500 || acceptedVersions.Any(version => string.IsNullOrWhiteSpace(version) || !SemanticReleaseVersion.TryParse(version.Trim(), out _)))
            throw new FactoryReleaseApiException("Choose published versions using MAJOR.MINOR.PATCH numbers.", StatusCodes.Status400BadRequest);
        if (acceptedVersions.Select(version => version.Trim()).Distinct(StringComparer.Ordinal).Count() != acceptedVersions.Count)
            throw new FactoryReleaseApiException("A version can only be selected once.", StatusCodes.Status400BadRequest);

        var state = await versions.GetVersionStateAsync(repositoryId, cancellationToken);
        var history = await ReadHistoryAsync(repository, cancellationToken);
        var observedTags = CandidateTags(history);
        var observedReleaseTags = history.PublishedReleases.Select(item => item.TagName).Order(StringComparer.Ordinal).ToArray();
        var selectable = observedTags.Concat(observedReleaseTags)
            .Select(tag => SemanticReleaseVersion.TryParseTag(tag, state.TagPrefix, out var version) ? version.ToString() : null)
            .Where(version => version is not null).Cast<string>()
            .Concat(state.PublishedVersions).ToHashSet(StringComparer.Ordinal);
        var normalizedAccepted = acceptedVersions.Select(version => version.Trim()).ToArray();
        var unobserved = normalizedAccepted.Where(version => !selectable.Contains(version)).ToArray();
        if (unobserved.Length > 0)
            throw new FactoryReleaseApiException($"These versions were not found in the observed history: {string.Join(", ", unobserved)}.", StatusCodes.Status400BadRequest);

        await versions.ReconcileVersionHistoryAsync(repositoryId, observedTags, observedReleaseTags,
            normalizedAccepted, reason, cancellationToken);
        return BuildVersionPlan(repositoryId, await versions.GetVersionStateAsync(repositoryId, cancellationToken), history);
    }

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
        if (targetBranch.Length is 0 or > 240 || targetBranch.Any(char.IsControl))
            throw new FactoryReleaseApiException("Enter a target branch between 1 and 240 characters.", StatusCodes.Status400BadRequest);
        if (request.GitHubMilestoneId is <= 0)
            throw new FactoryReleaseApiException("GitHub milestone identity must be a positive number.", StatusCodes.Status400BadRequest);

        var issueIds = request.GitHubIssueIds ?? [];
        if (issueIds.Count > 250 || issueIds.Any(id => id <= 0))
            throw new FactoryReleaseApiException("Select no more than 250 synchronized issues.", StatusCodes.Status400BadRequest);
        if (!SemanticReleaseVersion.TryParse(number, out var chosenVersion))
            throw new FactoryReleaseApiException("Enter a version as MAJOR.MINOR.PATCH, for example 2.4.0. Do not include the Git tag prefix.", StatusCodes.Status400BadRequest);

        var plan = await GetVersionPlanAsync(repository.Id, cancellationToken);
        if (plan.HistoryStatus == "DecisionRequired")
            throw new FactoryReleaseApiException(plan.HistoryMessage ?? "Review and reconcile this repository's published version history before planning a release.", StatusCodes.Status409Conflict);

        var reason = request.VersionReason?.Trim() ?? "";
        if (plan.LatestPublishedVersion is null)
        {
            if (reason != "initial-version")
                throw new FactoryReleaseApiException("This repository has no published version. Choose an explicit starting version.", StatusCodes.Status400BadRequest);
        }
        else
        {
            if (!TryGetChange(reason, out var change))
                throw new FactoryReleaseApiException("Choose whether this release contains bug fixes, new features, or breaking changes.", StatusCodes.Status400BadRequest);
            var latest = SemanticReleaseVersion.TryParse(plan.LatestPublishedVersion, out var parsedLatest) ? parsedLatest : default;
            var suggested = latest.Next(change);
            if (chosenVersion.CompareTo(latest) <= 0)
                throw new FactoryReleaseApiException($"A planned version must be newer than the latest published version {latest}.", StatusCodes.Status409Conflict);
            if (chosenVersion != suggested)
            {
                var overrideReason = request.VersionOverrideReason?.Trim() ?? "";
                if (overrideReason.Length < 10 || overrideReason.Length > MaxVersionReasonLength || overrideReason.Any(char.IsControl))
                    throw new FactoryReleaseApiException($"The selected {ReasonLabel(change)} version is {suggested}. Explain the override in 10 to 500 characters.", StatusCodes.Status400BadRequest);
            }
            else if (!string.IsNullOrWhiteSpace(request.VersionOverrideReason))
                throw new FactoryReleaseApiException("An override explanation is only used when choosing a version other than the suggestion.", StatusCodes.Status400BadRequest);
        }

        var versionReason = plan.LatestPublishedVersion is null ? "initial-version" : reason;
        var overrideText = string.IsNullOrWhiteSpace(request.VersionOverrideReason) ? null : request.VersionOverrideReason.Trim();
        var state = await versions.GetVersionStateAsync(repository.Id, cancellationToken);
        var plannedConflict = state.PlannedReleaseNumbers.FirstOrDefault(existing =>
            TryParsePlannedNumber(existing, out var existingVersion) && existingVersion == chosenVersion);
        if (plannedConflict is not null)
            throw new FactoryReleaseApiException($"Version {number} conflicts with the existing planned release number '{plannedConflict}'. Archive or rename that legacy plan before using this version.", StatusCodes.Status409Conflict);

        FactoryRelease? created;
        try
        {
            created = await releases.CreateAsync(new FactoryReleaseDraft(repository.Id, name, number, targetBranch,
                request.GitHubMilestoneId, versionReason, overrideText), issueIds, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new FactoryReleaseApiException(exception.Message, StatusCodes.Status409Conflict);
        }
        if (created is null)
            throw new FactoryReleaseApiException("This repository already has a planned release with that number.", StatusCodes.Status409Conflict);
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

    private async Task<RepositoryReleaseVersionHistory> ReadHistoryAsync(GitHubRepository repository, CancellationToken cancellationToken)
    {
        try { return await versionHistory.ReadAsync(repository, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            throw new FactoryReleaseApiException($"Could not reconcile published versions for {repository.Owner}/{repository.Name}: {exception.Message}", StatusCodes.Status502BadGateway);
        }
    }

    private static RepositoryReleaseVersionPlan BuildVersionPlan(long repositoryId, RepositoryReleaseVersionState state,
        RepositoryReleaseVersionHistory history)
    {
        var observedTags = CandidateTags(history);
        var observedReleaseTags = history.PublishedReleases.Select(item => item.TagName).Order(StringComparer.Ordinal).ToArray();
        var previous = state.LastReconciliation;
        var snapshotMatches = previous is not null &&
            previous.ObservedTags.SequenceEqual(observedTags, StringComparer.Ordinal) &&
            previous.ObservedReleaseTags.SequenceEqual(observedReleaseTags, StringComparer.Ordinal) &&
            previous.AcceptedVersions.Order(StringComparer.Ordinal).SequenceEqual(state.PublishedVersions.Order(StringComparer.Ordinal), StringComparer.Ordinal);

        var recognized = observedTags.Concat(observedReleaseTags)
            .Select(tag => SemanticReleaseVersion.TryParseTag(tag, state.TagPrefix, out var version) ? version.ToString() : null)
            .Where(version => version is not null).Cast<string>().ToArray();
        var unrecognizedTags = observedTags.Where(tag => !SemanticReleaseVersion.TryParseTag(tag, state.TagPrefix, out _)).ToArray();
        var unrecognizedReleaseTags = observedReleaseTags.Where(tag => !SemanticReleaseVersion.TryParseTag(tag, state.TagPrefix, out _)).ToArray();
        var duplicateReleaseTags = observedReleaseTags.GroupBy(tag => tag, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        var missingReleaseTags = observedReleaseTags.Where(tag => !observedTags.Contains(tag, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        var hasObservedHistory = observedTags.Length > 0 || observedReleaseTags.Length > 0;
        var decisionRequired = !snapshotMatches && (hasObservedHistory || state.PublishedVersions.Count > 0 || previous is not null);
        var messages = new List<string>();
        if (decisionRequired)
        {
            if (previous is null && hasObservedHistory)
                messages.Add("GitHub already has version-like tags or published Releases, but Factory has no confirmed history. Review and confirm the published versions.");
            else if (previous is not null)
                messages.Add("GitHub's current tag or Release history differs from the last confirmed snapshot. Review the change and confirm the published versions.");
            if (unrecognizedTags.Length + unrecognizedReleaseTags.Length > 0)
                messages.Add("Some observed version tags do not follow the vMAJOR.MINOR.PATCH convention; choose which valid versions belong in the history.");
            if (duplicateReleaseTags.Length > 0)
                messages.Add("GitHub returned duplicate published Release tags; confirm the intended history.");
            if (missingReleaseTags.Length > 0)
                messages.Add("At least one published GitHub Release references a tag that is not present in the repository tag list.");
        }

        var versions = state.PublishedVersions.Select(version => SemanticReleaseVersion.TryParse(version, out var parsed) ? parsed : (SemanticReleaseVersion?)null)
            .Where(version => version is not null).Select(version => version!.Value).Distinct().Order().ToArray();
        var latest = versions.LastOrDefault();
        var hasLatest = versions.Length > 0;
        var suggestions = !decisionRequired && hasLatest
            ? new[]
            {
                new ReleaseVersionSuggestion("bug-fixes", "Bug fixes", latest.Next(ReleaseVersionChange.BugFixes).ToString()),
                new ReleaseVersionSuggestion("new-features", "New features", latest.Next(ReleaseVersionChange.NewFeatures).ToString()),
                new ReleaseVersionSuggestion("breaking-changes", "Breaking changes", latest.Next(ReleaseVersionChange.BreakingChanges).ToString())
            }
            : [];

        var observedVersions = recognized.Distinct(StringComparer.Ordinal).Select(version =>
        {
            var versionTags = observedTags.Where(tag => SemanticReleaseVersion.TryParseTag(tag, state.TagPrefix, out var parsed) && parsed.ToString() == version).ToArray();
            var matchingReleases = history.PublishedReleases.Where(item => SemanticReleaseVersion.TryParseTag(item.TagName, state.TagPrefix, out var parsed) && parsed.ToString() == version).ToArray();
            return new ObservedReleaseVersion(version, versionTags.Length > 0, matchingReleases.Length > 0,
                matchingReleases.Where(item => item.PublishedAt is not null).Select(item => item.PublishedAt).Max(),
                matchingReleases.OrderByDescending(item => item.PublishedAt).FirstOrDefault()?.ReleaseId,
                matchingReleases.OrderByDescending(item => item.PublishedAt).FirstOrDefault()?.Url);
        }).OrderBy(item => SemanticReleaseVersion.TryParse(item.Version, out var parsed) ? parsed : default).ToArray();
        var legacyPlans = state.PlannedReleaseNumbers.Where(number => !SemanticReleaseVersion.TryParse(number, out _)).Distinct(StringComparer.Ordinal).ToArray();

        return new RepositoryReleaseVersionPlan(repositoryId, state.VersionFormat, state.TagPrefix,
            state.BreakingChangeDefinition, decisionRequired ? "DecisionRequired" : "Ready",
            messages.Count == 0 ? null : string.Join(" ", messages), hasLatest ? latest.ToString() : null,
            !hasLatest && !decisionRequired, suggestions, observedVersions, state.PublishedVersions,
            state.PlannedReleaseNumbers, legacyPlans, unrecognizedTags, unrecognizedReleaseTags, missingReleaseTags);
    }

    private static string[] CandidateTags(RepositoryReleaseVersionHistory history) =>
        history.VersionLikeTags.Where(IsVersionLike).Order(StringComparer.Ordinal).ToArray();

    private static bool IsVersionLike(string tag)
    {
        var value = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        return value.Length > 0 && char.IsAsciiDigit(value[0]);
    }

    private static bool TryGetChange(string reason, out ReleaseVersionChange change)
    {
        change = reason switch
        {
            "bug-fixes" => ReleaseVersionChange.BugFixes,
            "new-features" => ReleaseVersionChange.NewFeatures,
            "breaking-changes" => ReleaseVersionChange.BreakingChanges,
            _ => default
        };
        return reason is "bug-fixes" or "new-features" or "breaking-changes";
    }

    private static string ReasonLabel(ReleaseVersionChange change) => change switch
    {
        ReleaseVersionChange.BugFixes => "bug fixes",
        ReleaseVersionChange.NewFeatures => "new features",
        ReleaseVersionChange.BreakingChanges => "breaking changes",
        _ => "selected"
    };

    private static bool TryParsePlannedNumber(string value, out SemanticReleaseVersion version)
    {
        var normalized = value.StartsWith('v') || value.StartsWith('V') ? value[1..] : value;
        if (SemanticReleaseVersion.TryParse(normalized, out version)) return true;
        var parts = normalized.Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && major >= 0 && minor >= 0)
        {
            version = new SemanticReleaseVersion(major, minor, 0);
            return true;
        }
        return false;
    }

    private async Task<FactoryReleaseApiException> ActionConflictAsync(Guid id, string message, CancellationToken cancellationToken)
    {
        return await releases.GetAsync(id, cancellationToken) is null
            ? new FactoryReleaseApiException("Release not found.", StatusCodes.Status404NotFound)
            : new FactoryReleaseApiException(message, StatusCodes.Status409Conflict);
    }
}
