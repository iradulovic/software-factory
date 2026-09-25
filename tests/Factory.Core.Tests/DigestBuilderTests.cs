using System.Text.Json;

namespace Factory.Core.Tests;

public sealed class DigestBuilderTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly DateTimeOffset Until = DateTimeOffset.Parse("2026-01-02T00:00:00Z");

    private static DigestAlertCandidate Alert(string kind, string key, string detail, DateTimeOffset? updatedAt = null) =>
        new(kind, key, $"{kind} {key}", detail, null, null, updatedAt ?? Until);

    [Fact]
    public void Finished_work_is_always_included_in_full_never_deduplicated()
    {
        var finished = new List<DigestFinishedTask> { new(Guid.NewGuid(), "Task A", "owner/repo", 1, "https://example/pr/1", true, Until) };

        var payload = DigestBuilder.Build(Since, Until, finished, [], [], [], new Dictionary<string, string>());

        Assert.Same(finished, payload.FinishedWork);
    }

    [Fact]
    public void A_brand_new_alert_with_no_prior_fingerprint_is_surfaced()
    {
        var alert = Alert("Ci", "ci:1", "build failed");

        var payload = DigestBuilder.Build(Since, Until, [], [alert], [], [], new Dictionary<string, string>());

        Assert.Single(payload.CiFailures);
        Assert.Equal(1, payload.CiFailureTotal);
    }

    [Fact]
    public void An_alert_whose_fingerprint_is_unchanged_since_last_time_is_suppressed_but_still_counted()
    {
        var alert = Alert("Ci", "ci:1", "build failed");
        var previous = new Dictionary<string, string> { ["ci:1"] = "build failed" };

        var payload = DigestBuilder.Build(Since, Until, [], [alert], [], [], previous);

        Assert.Empty(payload.CiFailures);
        Assert.Equal(1, payload.CiFailureTotal);
    }

    [Fact]
    public void An_alert_whose_detail_changed_since_last_time_is_surfaced_again()
    {
        var alert = Alert("Ci", "ci:1", "a different check failed now");
        var previous = new Dictionary<string, string> { ["ci:1"] = "build failed" };

        var payload = DigestBuilder.Build(Since, Until, [], [alert], [], [], previous);

        Assert.Single(payload.CiFailures);
        Assert.Equal(1, payload.CiFailureTotal);
    }

    [Fact]
    public void Each_alert_section_is_deduplicated_independently_of_the_others()
    {
        var previous = new Dictionary<string, string> { ["ci:1"] = "same" };
        var ci = new List<DigestAlertCandidate> { Alert("Ci", "ci:1", "same") };
        var human = new List<DigestAlertCandidate> { Alert("Human", "human:1", "same") };

        var payload = DigestBuilder.Build(Since, Until, [], ci, human, [], previous);

        Assert.Empty(payload.CiFailures);
        Assert.Single(payload.NeedsHuman);
    }

    [Fact]
    public void Fingerprint_is_the_alerts_own_detail_text() =>
        Assert.Equal("some detail", DigestBuilder.Fingerprint(Alert("Quota", "quota:Codex", "some detail")));

    [Fact]
    public void Summary_counts_merged_failed_rejected_and_retry_events_and_compares_open_attention_to_previous_snapshot()
    {
        var previous = DigestBuilder.Build(Since.AddDays(-1), Since, [], [Alert("Ci", "ci:old", "old")], [], [],
            new Dictionary<string, string>());
        var work = new[]
        {
            new DigestFinishedTask(Guid.NewGuid(), "Merged", "owner/repo", 1, null, true, Until),
            new DigestFinishedTask(Guid.NewGuid(), "Rejected", "owner/repo", 2, null, false, Until),
            new DigestFinishedTask(Guid.NewGuid(), "Failed", "owner/repo", 3, null, false, Until, true)
        };
        var taskId = Guid.NewGuid();
        var retries = new DigestRetrySummary(3, [new DigestRetryTask(taskId, "Retried", "owner/repo", 4, "Pending", 2)]);
        var payload = DigestBuilder.Build(Since, Until, work, [Alert("Ci", "ci:new", "failure")], [], [Alert("Worker", "worker:1", "stale")],
            new Dictionary<string, string>(), retrySummary: retries, previousPayload: previous);

        Assert.Equal(new DigestChanges(1, 1, 1, 3, 1, 0), payload.ChangesSincePrevious);
        Assert.Contains("1 merged, 1 failed, 1 rejected, 3 retry transition(s)", payload.BriefingText);
        Assert.Contains("open attention up 1", payload.BriefingText);
    }

    [Fact]
    public void Action_items_rank_blockers_before_other_actions_and_include_task_and_pull_request_links()
    {
        var taskId = Guid.NewGuid();
        var worker = Alert("Worker", "worker:1", "stale heartbeat", Until.AddHours(-1));
        var ci = new DigestAlertCandidate("Ci", "ci:2", "CI is failing", "tests failed", taskId,
            "https://github.example/pull/2", Until);
        var human = new DigestAlertCandidate("Human", "human:3", "Needs a decision", "ambiguous", taskId, null, Until);

        var payload = DigestBuilder.Build(Since, Until, [], [ci], [human], [worker], new Dictionary<string, string>(),
            dashboardBaseUrl: "https://factory.example/");

        Assert.Equal("worker:1", payload.ActionItems![0].Key);
        Assert.Equal("https://factory.example/", payload.ActionItems[0].Url);
        Assert.Contains(payload.ActionItems, item => item.Key == "ci:2" && item.Url == "https://github.example/pull/2");
        Assert.Contains(payload.ActionItems, item => item.Key == "human:3" && item.Url == $"https://factory.example/tasks/{taskId}");
    }

    [Fact]
    public void Ci_action_without_pull_request_url_links_to_dashboard_task()
    {
        var taskId = Guid.NewGuid();
        var ci = new DigestAlertCandidate("Ci", "ci:task", "CI is failing", "tests failed", taskId, null, Until);

        var payload = DigestBuilder.Build(Since, Until, [], [ci], [], [], new Dictionary<string, string>(),
            dashboardBaseUrl: "https://factory.example/");

        var action = Assert.Single(payload.ActionItems!);
        Assert.Equal(taskId, action.TaskId);
        Assert.Equal($"https://factory.example/tasks/{taskId}", action.Url);
    }

    [Fact]
    public void Zero_work_day_produces_an_accurate_useful_idle_briefing()
    {
        var providers = new[] { new DigestProviderStatus("Codex", "Available", "1.2.3", null, false, null, null, null, Until) };

        var payload = DigestBuilder.Build(Since, Until, [], [], [], [], new Dictionary<string, string>(), providers: providers);

        Assert.Contains("All idle", payload.BriefingText);
        Assert.Contains("no open attention", payload.BriefingText);
        Assert.Contains("Codex available, quota clear", payload.BriefingText);
        Assert.Empty(payload.ActionItems!);
    }

    [Fact]
    public void Legacy_stored_payloads_without_new_fields_remain_readable()
    {
        const string json = """
            {"WindowSince":"2026-01-01T00:00:00Z","WindowUntil":"2026-01-02T00:00:00Z","FinishedWork":[],
             "CiFailures":[],"CiFailureTotal":0,"NeedsHuman":[],"NeedsHumanTotal":0,"Blockers":[],"BlockerTotal":0}
            """;

        var payload = JsonSerializer.Deserialize<DigestPayload>(json);

        Assert.NotNull(payload);
        Assert.Null(payload.RetrySummary);
        Assert.Null(payload.BriefingText);
    }

    [Fact]
    public void An_unchanged_provider_outage_is_not_repeated_as_an_action_on_the_next_digest()
    {
        var unavailable = new[] { new DigestProviderStatus("Claude", "Unavailable", null, "CLI not found", false, null, null, null, Until) };
        var previous = DigestBuilder.Build(Since, Until, [], [], [], [], new Dictionary<string, string>(), providers: unavailable);

        var current = DigestBuilder.Build(Until, Until.AddDays(1), [], [], [], [], new Dictionary<string, string>(),
            providers: unavailable, previousPayload: previous);

        Assert.Contains(previous.ActionItems!, action => action.Key == "provider-unavailable:claude");
        Assert.DoesNotContain(current.ActionItems!, action => action.Key == "provider-unavailable:claude");
        Assert.Equal(0, current.ChangesSincePrevious?.ProviderStateChanges);
    }
}
