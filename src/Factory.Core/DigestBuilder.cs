using System.Globalization;
using System.Text;

namespace Factory.Core;

/// <summary>Builds the factual daily operator briefing from records and point-in-time provider checks supplied by
/// the caller. This type has no database, clock, process, or agent access so the briefing remains deterministic and
/// independently testable.</summary>
public static class DigestBuilder
{
    public static DigestPayload Build(
        DateTimeOffset windowSince, DateTimeOffset windowUntil,
        IReadOnlyList<DigestFinishedTask> finishedWork,
        IReadOnlyList<DigestAlertCandidate> ciFailures,
        IReadOnlyList<DigestAlertCandidate> needsHuman,
        IReadOnlyList<DigestAlertCandidate> blockers,
        IReadOnlyDictionary<string, string> previousFingerprints,
        IReadOnlyList<DigestAlertCandidate>? failedTasks = null,
        DigestRetrySummary? retrySummary = null,
        DigestNextTask? nextEligibleTask = null,
        IReadOnlyList<DigestProviderStatus>? providers = null,
        DigestPayload? previousPayload = null,
        string dashboardBaseUrl = "http://localhost:3000")
    {
        failedTasks ??= [];
        retrySummary ??= new DigestRetrySummary(0, []);
        providers ??= [];

        var surfacedCi = Surfaced(ciFailures, previousFingerprints);
        var surfacedHuman = Surfaced(needsHuman, previousFingerprints);
        var surfacedBlockers = Surfaced(blockers, previousFingerprints);
        var surfacedFailures = Surfaced(failedTasks, previousFingerprints);
        var openAttention = ciFailures.Count + needsHuman.Count + blockers.Count + failedTasks.Count;
        var previousOpenAttention = previousPayload is null ? null :
            (int?)(previousPayload.CiFailureTotal + previousPayload.NeedsHumanTotal + previousPayload.BlockerTotal + previousPayload.FailedTaskTotal);
        var providerChanges = previousPayload?.Providers is null ? (int?)null : CountProviderStateChanges(previousPayload.Providers, providers);
        var changes = new DigestChanges(
            finishedWork.Count(item => item.Merged),
            finishedWork.Count(item => !item.Merged && !item.Failed),
            finishedWork.Count(item => item.Failed),
            retrySummary.TotalRetries,
            previousOpenAttention is null ? null : openAttention - previousOpenAttention.Value,
            providerChanges);

        var absoluteNextTask = nextEligibleTask is null ? null : nextEligibleTask with
        {
            Url = DashboardUrl(dashboardBaseUrl, $"/tasks/{nextEligibleTask.TaskId}")
        };
        var actions = RankActions(
            surfacedCi.Concat(surfacedHuman).Concat(surfacedBlockers).Concat(surfacedFailures),
            retrySummary.Tasks, providers, previousPayload, dashboardBaseUrl);

        var payload = new DigestPayload(
            windowSince, windowUntil, finishedWork,
            surfacedCi, ciFailures.Count,
            surfacedHuman, needsHuman.Count,
            surfacedBlockers, blockers.Count,
            surfacedFailures, failedTasks.Count,
            absoluteNextTask, retrySummary, providers, changes, actions, null);
        return payload with { BriefingText = FormatBriefing(payload) };
    }

    private static IReadOnlyList<DigestAlertCandidate> Surfaced(
        IReadOnlyList<DigestAlertCandidate> candidates,
        IReadOnlyDictionary<string, string> previousFingerprints) => candidates
        .Where(candidate => !previousFingerprints.TryGetValue(candidate.Key, out var previous) || previous != Fingerprint(candidate))
        .OrderByDescending(candidate => candidate.UpdatedAt)
        .ToList();

    private static IReadOnlyList<DigestActionItem> RankActions(
        IEnumerable<DigestAlertCandidate> surfacedAlerts,
        IReadOnlyList<DigestRetryTask> retriedTasks,
        IReadOnlyList<DigestProviderStatus> providers,
        DigestPayload? previousPayload,
        string dashboardBaseUrl)
    {
        var actions = surfacedAlerts.Select(candidate => new DigestActionItem(
            candidate.Key, candidate.Kind, candidate.Title, candidate.Detail, candidate.TaskId,
            ActionUrl(candidate, dashboardBaseUrl), ActionPriority(candidate.Kind), candidate.UpdatedAt));
        var retries = retriedTasks.Where(task => task.RetryCount > 0).Select(task => new DigestActionItem(
            $"retry:{task.TaskId}", "Retries", task.Title,
            $"{task.RetryCount} retry transition{(task.RetryCount == 1 ? "" : "s")} in this window; current status: {task.Status}.",
            task.TaskId, DashboardUrl(dashboardBaseUrl, $"/tasks/{task.TaskId}"), 2, DateTimeOffset.MinValue));
        var previousProviders = previousPayload?.Providers?.ToDictionary(provider => provider.Provider, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DigestProviderStatus>(StringComparer.OrdinalIgnoreCase);
        var unavailableProviders = providers.Where(provider => provider.Availability != "Available" && !provider.QuotaDetected)
            .Where(provider => !previousProviders.TryGetValue(provider.Provider, out var previous)
                || previous.Availability != provider.Availability || previous.Error != provider.Error || previous.QuotaDetected != provider.QuotaDetected)
            .Select(provider => new DigestActionItem(
                $"provider-unavailable:{provider.Provider.ToLowerInvariant()}", "Provider",
                provider.Availability == "Unknown" ? $"{provider.Provider} availability unknown" : $"{provider.Provider} CLI unavailable",
                provider.Error ?? "The configured provider CLI did not pass its availability check.", null,
                DashboardUrl(dashboardBaseUrl, "/"), 1, provider.CheckedAt));
        return actions.Concat(retries).Concat(unavailableProviders)
            .OrderBy(action => action.Priority)
            .ThenBy(action => action.UpdatedAt)
            .ThenBy(action => action.Key, StringComparer.Ordinal)
            .Take(5)
            .ToList();
    }

    private static int CountProviderStateChanges(
        IReadOnlyList<DigestProviderStatus> previous,
        IReadOnlyList<DigestProviderStatus> current)
    {
        var previousByName = previous.ToDictionary(provider => provider.Provider, StringComparer.OrdinalIgnoreCase);
        var currentByName = current.ToDictionary(provider => provider.Provider, StringComparer.OrdinalIgnoreCase);
        return previousByName.Keys.Union(currentByName.Keys, StringComparer.OrdinalIgnoreCase)
            .Count(name => !previousByName.TryGetValue(name, out var oldStatus)
                || !currentByName.TryGetValue(name, out var newStatus)
                || oldStatus.Availability != newStatus.Availability
                || oldStatus.QuotaDetected != newStatus.QuotaDetected
                || oldStatus.QuotaResetAt != newStatus.QuotaResetAt);
    }

    private static int ActionPriority(string kind) => kind switch
    {
        "Worker" or "Pause" or "ReviewBacklog" or "Quota" or "RepairsStopped" or "MergeConflict" => 0,
        "Human" or "Failed" or "Rejected" or "Ci" or "RepositorySync" => 1,
        _ => 2
    };

    private static string? ActionUrl(DigestAlertCandidate candidate, string dashboardBaseUrl)
    {
        if (candidate.Url is { Length: > 0 } url)
            return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : DashboardUrl(dashboardBaseUrl, url);
        return candidate.TaskId is { } taskId ? DashboardUrl(dashboardBaseUrl, $"/tasks/{taskId}") : DashboardUrl(dashboardBaseUrl, "/");
    }

    private static string DashboardUrl(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    /// <summary>A stable content fingerprint for one alert candidate — deliberately just its own detail text.</summary>
    public static string Fingerprint(DigestAlertCandidate candidate) => candidate.Detail;

    private static string FormatBriefing(DigestPayload payload)
    {
        var text = new StringBuilder();
        text.AppendLine("Software Factory operator briefing");
        text.Append("Window (UTC, start inclusive/end exclusive): ").Append(FormatTime(payload.WindowSince)).Append(" – ").AppendLine(FormatTime(payload.WindowUntil));

        var changes = payload.ChangesSincePrevious!;
        text.Append("Work in window: ").Append(changes.Merged).Append(" merged, ")
            .Append(changes.Failed).Append(" failed, ").Append(changes.Rejected).Append(" rejected, ")
            .Append(changes.Retries).AppendLine(" retry transition(s).");

        var openAttention = payload.CiFailureTotal + payload.NeedsHumanTotal + payload.BlockerTotal + payload.FailedTaskTotal;
        text.Append("Open attention now: ").Append(openAttention).Append(" (")
            .Append(payload.CiFailureTotal).Append(" CI, ").Append(payload.NeedsHumanTotal).Append(" needs-human, ")
            .Append(payload.FailedTaskTotal).Append(" failed/rejected, ").Append(payload.BlockerTotal).AppendLine(" other blocker(s)).");
        text.AppendLine(changes.OpenAttentionDelta is null
            ? "Change since previous digest: first recorded snapshot."
            : $"Change since previous digest: open attention {Signed(changes.OpenAttentionDelta.Value)}; " +
              (changes.ProviderStateChanges is { } changedProviders ? $"provider state changed for {changedProviders} provider(s); " : "provider state comparison unavailable; ") +
              "work and retries above cover this window.");

        if (payload.NextEligibleTask is { } next)
        {
            text.Append("Next eligible: ").Append(next.Title).Append(" — ").Append(next.Repository);
            if (next.IssueNumber is { } issueNumber) text.Append(" #").Append(issueNumber);
            text.Append(" (").Append(next.Url).AppendLine(").");
        }
        else
        {
            text.AppendLine("Next eligible: no task is currently eligible to claim.");
        }

        if (payload.Providers is not { Count: > 0 })
        {
            text.AppendLine("Providers: no configured provider checks were recorded.");
        }
        else
        {
            text.Append("Providers at generation: ").AppendLine(string.Join("; ", payload.Providers.Select(FormatProvider)) + ".");
        }

        if (payload.ActionItems is not { Count: > 0 })
        {
            var noWindowActivity = changes.Merged == 0 && changes.Failed == 0 && changes.Rejected == 0 && changes.Retries == 0;
            var allProvidersReady = payload.Providers is { Count: > 0 } && payload.Providers.All(provider =>
                provider.Availability == "Available" && !provider.QuotaDetected);
            if (openAttention == 0 && noWindowActivity && payload.NextEligibleTask is null && allProvidersReady)
                text.AppendLine("All idle: no finished work or retries in this window, no open attention, and no currently eligible task; configured providers are available.");
            else if (openAttention == 0 && noWindowActivity && payload.NextEligibleTask is null)
                text.AppendLine("No work finished or retried in this window, no attention items are open, and no task is currently eligible. Check the provider snapshot for dispatch readiness.");
            else
                text.AppendLine("Top actions: none newly opened or changed since the previous digest.");
        }
        else
        {
            text.AppendLine("Top actions:");
            for (var index = 0; index < payload.ActionItems.Count; index++)
            {
                var item = payload.ActionItems[index];
                text.Append(index + 1).Append(". ").Append(item.Title).Append(" — ").Append(item.Detail);
                if (item.Url is { Length: > 0 } url) text.Append(" ").Append(url);
                text.AppendLine();
            }
        }
        return text.ToString().TrimEnd();
    }

    private static string FormatProvider(DigestProviderStatus provider)
    {
        var description = $"{provider.Provider} {provider.Availability.ToLowerInvariant()}";
        if (provider.QuotaDetected)
            description += provider.QuotaResetAt is { } resetAt
                ? $", quota resets {FormatTime(resetAt)}"
                : ", quota reset unknown";
        else
            description += ", quota clear";
        if (provider.Error is { Length: > 0 } error) description += $" ({error})";
        return description;
    }

    private static string Signed(int value) => value > 0 ? $"up {value}" : value < 0 ? $"down {Math.Abs(value)}" : "unchanged";
    private static string FormatTime(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
