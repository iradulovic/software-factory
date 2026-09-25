using System.Net.Http.Json;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Factory.GitHubSync;

/// <summary>Periodically builds and persists a factual operator briefing with outcomes, retries, actionable tasks,
/// provider checks, and current blockers. Generation itself never repeats an unchanged alert — see
/// <see cref="DigestBuilder"/> — and delivery to an external destination only ever happens when one is
/// explicitly configured (<see cref="DigestOptions.WebhookUrl"/>); otherwise the digest is still generated and
/// persisted, just readable only via <c>GET /api/digest</c>.</summary>
public sealed class DigestWorker(ITaskStore tasks, IDigestStore digests, IClock clock, IHttpClientFactory httpClientFactory,
    IOptions<DigestOptions> options, IOptions<FactoryOptions> factoryOptions,
    IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, ILogger<DigestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await GenerateIfDueAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Digest generation cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollingIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>Generates and persists a new digest only once <see cref="DigestOptions.IntervalHours"/> has
    /// elapsed since the last one, regardless of how often the poll loop itself wakes up — keeps a "daily digest"
    /// actually daily without a separate cron-like scheduler. The finished-work window always starts exactly where
    /// the previous digest's generation left off, so consecutive digests can never overlap or gap.</summary>
    private async Task GenerateIfDueAsync(CancellationToken cancellationToken)
    {
        var latest = await digests.GetLatestAsync(cancellationToken);
        var now = clock.UtcNow;
        if (latest is not null && now - latest.GeneratedAt < TimeSpan.FromHours(options.Value.IntervalHours)) return;
        var windowSince = latest?.GeneratedAt ?? now.AddHours(-options.Value.IntervalHours);

        using var activity = FactoryTelemetry.Source.StartActivity("digest.generate");

        var finishedWork = await tasks.GetRecentlyFinishedTasksAsync(windowSince, now, cancellationToken);
        var ciFailures = await tasks.GetOpenCiFailureAlertsAsync(cancellationToken);
        var needsHuman = await tasks.GetNeedsHumanAlertsAsync(cancellationToken);
        var blockers = await tasks.GetActiveBlockerAlertsAsync(cancellationToken);
        var failedTasks = await tasks.GetOpenFailedTaskAlertsAsync(cancellationToken);
        var retrySummary = await tasks.GetDigestRetrySummaryAsync(windowSince, now, cancellationToken);
        var nextEligibleTask = await tasks.GetNextEligibleTaskAsync(cancellationToken);
        var providers = await ReadProviderStatusesAsync(cancellationToken);
        var previousFingerprints = await digests.GetAlertFingerprintsAsync(cancellationToken);

        var payload = DigestBuilder.Build(windowSince, now, finishedWork, ciFailures, needsHuman, blockers,
            previousFingerprints, failedTasks, retrySummary, nextEligibleTask, providers, latest?.Payload,
            factoryOptions.Value.DashboardBaseUrl);
        var openAlerts = ciFailures.Concat(needsHuman).Concat(blockers).Concat(failedTasks).ToList();
        var digest = await digests.SaveAsync(payload, openAlerts, cancellationToken);

        logger.LogInformation(
            "Generated digest {DigestId}: {FinishedCount} finished, {CiFailureTotal} CI failure(s) open ({CiFailureSurfaced} new/changed), " +
            "{NeedsHumanTotal} needing the developer ({NeedsHumanSurfaced} new/changed), {BlockerTotal} blocker(s) open ({BlockerSurfaced} new/changed)",
            digest.Id, payload.FinishedWork.Count,
            payload.CiFailureTotal, payload.CiFailures.Count,
            payload.NeedsHumanTotal, payload.NeedsHuman.Count,
            payload.BlockerTotal, payload.Blockers.Count);

        if (options.Value.WebhookUrl is { Length: > 0 } webhookUrl)
            await DeliverAsync(digest, webhookUrl, cancellationToken);
    }

    private async Task<IReadOnlyList<DigestProviderStatus>> ReadProviderStatusesAsync(CancellationToken cancellationToken)
    {
        var providers = new List<DigestProviderStatus>();
        foreach (var group in availabilityCheckers.GroupBy(checker => checker.Provider, StringComparer.OrdinalIgnoreCase))
        {
            var checker = group.First();
            string state;
            string? version = null;
            string? error = null;
            try
            {
                var result = await checker.CheckAsync(cancellationToken);
                state = result.Available ? "Available" : "Unavailable";
                version = result.Version;
                error = result.Error;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state = "Unknown";
                error = ex.Message;
                logger.LogWarning(ex, "Digest availability check failed for provider {Provider}", checker.Provider);
            }

            var checkedAt = clock.UtcNow;
            var atQuota = await tasks.IsAgentAtQuotaAsync(checker.Provider, cancellationToken);
            var quota = await tasks.GetAgentQuotaStatusAsync(checker.Provider, cancellationToken);
            providers.Add(new DigestProviderStatus(checker.Provider, state, version, Limit(error, 240), atQuota,
                atQuota ? quota?.ResetAt : null, atQuota ? quota?.Window.ToString() : null,
                atQuota ? quota?.ResetKind.ToString() : null, checkedAt));
        }
        return providers.OrderBy(provider => provider.Provider, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Limit(string? value, int length) => value is { Length: > 0 }
        ? value.Length <= length ? value : value[..length]
        : null;

    private async Task DeliverAsync(DigestRun digest, string webhookUrl, CancellationToken cancellationToken)
    {
        using var activity = FactoryTelemetry.Source.StartActivity("digest.deliver");
        try
        {
            var client = httpClientFactory.CreateClient(nameof(DigestWorker));
            var response = await client.PostAsJsonAsync(webhookUrl, digest.Payload, cancellationToken);
            var error = response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
            await digests.RecordDeliveryAsync(digest.Id, webhookUrl, response.IsSuccessStatusCode, error, cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Digest delivery to {WebhookUrl} failed with HTTP {StatusCode}", webhookUrl, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await digests.RecordDeliveryAsync(digest.Id, webhookUrl, false, ex.Message, cancellationToken);
            logger.LogError(ex, "Digest delivery to {WebhookUrl} failed", webhookUrl);
        }
    }
}
