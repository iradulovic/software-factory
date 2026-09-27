using System.Net.Http.Json;
using Dapper;
using Factory.Core;
using Factory.Infrastructure;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Factory.Api;

public sealed class NudgeWorker(NpgsqlDataSource db, NudgeStore store, ITaskStore tasks,
    IEnumerable<IAgentAvailabilityChecker> availabilityCheckers, IOptions<FactoryOptions> options,
    IOptions<GitHubSyncOptions> githubOptions, IOptions<DigestOptions> digestOptions,
    IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<NudgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Nudge cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PollAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var c = await db.OpenConnectionAsync(ct);
        var taskRows = await c.QueryAsync<AttentionTaskRow>(new CommandDefinition(AttentionQuery.Tasks, cancellationToken: ct));
        var sources = (await c.QueryAsync<AttentionSourceRow>(new CommandDefinition(AttentionQuery.Sources, cancellationToken: ct))).ToList();
        var reviewLimit = options.Value.MaxOutstandingReviewWork;
        if (reviewLimit > 0)
        {
            var reviewCount = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*)::int FROM factory.task WHERE status IN ('ReadyForPublish','Published')", cancellationToken: ct));
            if (reviewCount >= reviewLimit)
                sources.Add(new AttentionSourceRow { Id = "global", Kind = "ReviewBacklog", Title = "Review backlog limit reached",
                    Reason = $"{reviewCount}/{reviewLimit} tasks await publication or merge.", FirstObservedAt = now, LastObservedAt = now });
        }
        var latestWorker = await c.ExecuteScalarAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT max(last_seen_at) FROM factory.worker", cancellationToken: ct));
        var staleAfter = TimeSpan.FromSeconds(Math.Max(options.Value.PollingIntervalSeconds, options.Value.LeaseHeartbeatSeconds) * 3);
        if (AttentionProjection.StaleWorker(latestWorker, now, staleAfter) is { } worker)
            sources.Add(worker);

        var checkers = availabilityCheckers.ToList();
        if (checkers.Count > 0)
        {
            var anyAvailable = false;
            foreach (var checker in checkers)
            {
                try
                {
                    var availability = await checker.CheckAsync(ct);
                    if (availability.Available && !await tasks.IsAgentAtQuotaAsync(checker.Provider, ct)
                        && !(await tasks.GetDispatchPauseAsync(checker.Provider, ct)).Paused)
                        anyAvailable = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Agent availability check failed for {Provider}: {ErrorType}", checker.Provider, ex.GetType().Name);
                }
            }
            if (!anyAvailable)
                sources.Add(new AttentionSourceRow { Id = "global", Kind = "AllAgentsUnavailable",
                    Title = "No agent can claim work", Reason = "Every configured agent is quota blocked, paused, unavailable, or unauthenticated.",
                    FirstObservedAt = now, LastObservedAt = now });
        }
        var items = AttentionProjection.ForTasks(taskRows, now, githubOptions.Value.MaxCiRepairAttempts)
            .Concat(AttentionProjection.ForSources(sources));
        var webhookUrl = digestOptions.Value.WebhookUrl;
        await store.ReconcileAsync(NudgePolicy.Select(items), now, !string.IsNullOrWhiteSpace(webhookUrl), ct);
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        // One due delivery per cycle, with a persisted lease. A retry keeps the same notification ID.
        var due = await store.ClaimDeliveryAsync(now, ct);
        if (due is null) return;
        try
        {
            var dashboard = configuration["Dashboard:Url"] ?? "http://localhost:3000";
            var url = $"{dashboard.TrimEnd('/')}/{due.Href.TrimStart('/')}";
            using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
            {
                Content = JsonContent.Create(new { id = due.Id, kind = due.Kind, what = due.Title,
                    why = due.Explanation, url, occurredAt = due.OccurredAt })
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", due.Id.ToString());
            using var response = await httpClientFactory.CreateClient(nameof(NudgeWorker)).SendAsync(request, ct);
            await store.RecordDeliveryAsync(due.Id, response.IsSuccessStatusCode,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}", DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not persist or log exception messages: a URL may contain a credential.
            await store.RecordDeliveryAsync(due.Id, false, ex.GetType().Name, DateTimeOffset.UtcNow, ct);
            logger.LogWarning("Nudge delivery failed for {NudgeId}: {ErrorType}", due.Id, ex.GetType().Name);
        }
    }
}
