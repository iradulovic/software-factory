using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Factory.Core;
using Microsoft.Extensions.Options;

namespace Factory.Api;

public sealed class AgentUsageOptions
{
    public int PollingIntervalSeconds { get; set; } = 300;
    public int HttpTimeoutSeconds { get; set; } = 10;
    public double WarningThresholdPercent { get; set; } = 80;
    public double CriticalThresholdPercent { get; set; } = 95;
    public string? CodexHome { get; set; }
    public string? ClaudeCredentialsPath { get; set; }
    public string ClaudeUsageEndpoint { get; set; } = "https://api.anthropic.com/api/oauth/usage";
}

public sealed record UsageWindow(double UsedPercent, DateTimeOffset ResetsAt);

public sealed record UsageSnapshot(string Provider, bool IsKnown, UsageWindow? FiveHour, UsageWindow? Weekly,
    DateTimeOffset CapturedAt, string? UnknownReason)
{
    public bool IsStale { get; init; }

    public static UsageSnapshot Unknown(string provider, DateTimeOffset capturedAt, string reason) =>
        new(provider, false, null, null, capturedAt, reason);

    public static UsageSnapshot Known(string provider, UsageWindow fiveHour, UsageWindow weekly, DateTimeOffset capturedAt) =>
        new(provider, true, fiveHour, weekly, capturedAt, null);
}

public interface IAgentUsageProvider
{
    string Provider { get; }
    Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken);
}

public interface IAgentUsageSnapshotStore
{
    UsageSnapshot GetOrUnknown(string provider, DateTimeOffset now);
    void Set(UsageSnapshot snapshot);
}

public sealed class AgentUsageSnapshotStore : IAgentUsageSnapshotStore
{
    private readonly ConcurrentDictionary<string, UsageSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

    public UsageSnapshot GetOrUnknown(string provider, DateTimeOffset now) => snapshots.TryGetValue(provider, out var snapshot)
        ? snapshot : UsageSnapshot.Unknown(provider, now, "Usage has not been checked yet");

    public void Set(UsageSnapshot snapshot) => snapshots.AddOrUpdate(snapshot.Provider,
        snapshot,
        (_, previous) => !snapshot.IsKnown && previous.IsKnown
            ? previous with { IsStale = true }
            : snapshot with { IsStale = false });
}

public sealed class CodexUsageProvider(IOptions<AgentUsageOptions> options, IClock clock) : IAgentUsageProvider
{
    public string Provider => "Codex";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        try
        {
            var home = options.Value.CodexHome;
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var sessions = Path.Combine(home, "sessions");
            if (!Directory.Exists(sessions)) return UsageSnapshot.Unknown(Provider, now, "No Codex session data is available");

            var latest = Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(path => new { Path = path, LastWrite = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(file => file.LastWrite).FirstOrDefault();
            if (latest is null) return UsageSnapshot.Unknown(Provider, now, "No Codex rollout file is available");

            UsageSnapshot? snapshot = null;
            var malformedEvent = false;
            await using var stream = new FileStream(latest.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException)
                {
                    // The active Codex process may be halfway through appending this JSONL record.
                    // Keep any complete usage event already read from the same rollout.
                    malformedEvent = true;
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        malformedEvent = true;
                        continue;
                    }
                    var payload = root.TryGetProperty("payload", out var nestedPayload) ? nestedPayload : root;
                    if (payload.ValueKind != JsonValueKind.Object)
                    {
                        malformedEvent = true;
                        continue;
                    }
                    if (!payload.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "token_count") continue;
                    if (!payload.TryGetProperty("rate_limits", out var limits)
                        || !TryReadWindow(limits, "primary", out var fiveHour)
                        || !TryReadWindow(limits, "secondary", out var weekly))
                    {
                        malformedEvent = true;
                        continue;
                    }
                    snapshot = UsageSnapshot.Known(Provider, fiveHour!, weekly!, now);
                }
            }
            return snapshot ?? UsageSnapshot.Unknown(Provider, now, malformedEvent
                ? "The latest Codex usage event is malformed"
                : "The latest Codex rollout has no usage event");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UsageSnapshot.Unknown(Provider, now, "Codex usage data could not be read"); }
    }

    private static bool TryReadWindow(JsonElement limits, string name, out UsageWindow? window)
    {
        window = null;
        if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("used_percent", out var usedElement) || !usedElement.TryGetDouble(out var used)
            || !value.TryGetProperty("resets_at", out var resetElement) || !resetElement.TryGetInt64(out var reset)
            || !double.IsFinite(used) || used is < 0 or > 100) return false;
        try { window = new UsageWindow(used, DateTimeOffset.FromUnixTimeSeconds(reset)); }
        catch (ArgumentOutOfRangeException) { return false; }
        return true;
    }
}

public sealed class ClaudeUsageProvider(IOptions<AgentUsageOptions> options, IHttpClientFactory httpClientFactory, IClock clock) : IAgentUsageProvider
{
    public string Provider => "Claude";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        try
        {
            var path = options.Value.ClaudeCredentialsPath;
            if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            if (!File.Exists(path)) return UsageSnapshot.Unknown(Provider, now, "Claude credentials are unavailable");

            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var tokenElement) || string.IsNullOrWhiteSpace(tokenElement.GetString())
                || !oauth.TryGetProperty("expiresAt", out var expiresElement) || !expiresElement.TryGetInt64(out var expiresAt)
                || CredentialExpiry(expiresAt) <= now)
                return UsageSnapshot.Unknown(Provider, now, "Claude credentials are missing or expired");

            using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.ClaudeUsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenElement.GetString());
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.HttpTimeoutSeconds)));
            using var response = await httpClientFactory.CreateClient(nameof(ClaudeUsageProvider)).SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return UsageSnapshot.Unknown(Provider, now, $"Claude usage endpoint returned HTTP {(int)response.StatusCode}");

            using var usage = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!TryReadWindow(usage.RootElement, "five_hour", out var fiveHour)
                || !TryReadWindow(usage.RootElement, "seven_day", out var weekly))
                return UsageSnapshot.Unknown(Provider, now, "Claude usage response is malformed");
            return UsageSnapshot.Known(Provider, fiveHour!, weekly!, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return UsageSnapshot.Unknown(Provider, now, "Claude usage could not be read"); }
    }

    private static DateTimeOffset CredentialExpiry(long value) => value > 10_000_000_000
        ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);

    private static bool TryReadWindow(JsonElement root, string name, out UsageWindow? window)
    {
        window = null;
        if (!root.TryGetProperty(name, out var value)
            || !value.TryGetProperty("utilization", out var usedElement) || !usedElement.TryGetDouble(out var used)
            || !double.IsFinite(used) || used is < 0 or > 100
            || !value.TryGetProperty("resets_at", out var resetElement)
            || !DateTimeOffset.TryParse(resetElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset)) return false;
        window = new UsageWindow(used, reset);
        return true;
    }
}

public sealed class AgentUsageWorker(IEnumerable<IAgentUsageProvider> providers, IAgentUsageSnapshotStore snapshots,
    IOptions<AgentUsageOptions> options, ILogger<AgentUsageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(30, options.Value.PollingIntervalSeconds)));
        do { await PollAsync(stoppingToken); } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            try { snapshots.Set(await provider.GetUsageAsync(cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning("Agent usage check failed for {Provider}: {ErrorType}", provider.Provider, ex.GetType().Name); }
        }
    }
}
