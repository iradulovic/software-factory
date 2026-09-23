using System.Net.Http.Json;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Factory.GitHubSync;

/// <summary>Periodically builds and persists a digest summarizing finished work, CI failures, items needing the
/// developer, and meaningful quota/worker blockers (SF-705). Generation itself never repeats an unchanged alert —
/// see <see cref="DigestBuilder"/> — and delivery to an external destination only ever happens when one is
/// explicitly configured (<see cref="DigestOptions.WebhookUrl"/>); otherwise the digest is still generated and
/// persisted, just readable only via <c>GET /api/digest</c>.</summary>
public sealed class DigestWorker(ITaskStore tasks, IDigestStore digests, IClock clock, IHttpClientFactory httpClientFactory,
    IOptions<DigestOptions> options, ILogger<DigestWorker> logger) : BackgroundService
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

        var finishedWork = await tasks.GetRecentlyFinishedTasksAsync(windowSince, cancellationToken);
        var ciFailures = await tasks.GetOpenCiFailureAlertsAsync(cancellationToken);
        var needsHuman = await tasks.GetNeedsHumanAlertsAsync(cancellationToken);
        var blockers = await tasks.GetActiveBlockerAlertsAsync(cancellationToken);
        var previousFingerprints = await digests.GetAlertFingerprintsAsync(cancellationToken);

        var payload = DigestBuilder.Build(windowSince, now, finishedWork, ciFailures, needsHuman, blockers, previousFingerprints);
        var openAlerts = ciFailures.Concat(needsHuman).Concat(blockers).ToList();
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
