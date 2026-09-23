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
}
