namespace Factory.Core.Tests;

public sealed class QuotaClassifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static AgentProfile Profile(IReadOnlyList<string>? weeklySignatures = null, int weeklyCooldownHours = 168, string? resetPattern = null) =>
        new("Codex", "codex", ["exec"], "stdin", 90, ["quota", "usage limit"], ["--version"], 5, 5, weeklySignatures, weeklyCooldownHours, resetPattern);

    private static ProcessResult Process(int? exitCode, string stdout = "", string stderr = "") =>
        new("codex", [], ".", Now, Now, exitCode, stdout, stderr, false, false);

    [Fact]
    public void A_successful_process_is_never_flagged_even_when_its_output_mentions_a_signature()
    {
        var process = Process(0, stdout: "Implemented the quota dashboard feature.");

        var signal = QuotaClassifier.Classify(Profile(), process, Now);

        Assert.False(signal.Detected);
        Assert.Equal(QuotaSignal.None, signal);
    }

    [Fact]
    public void A_successful_process_is_never_flagged_even_if_stderr_mentions_a_signature()
    {
        var process = Process(0, stderr: "warning: usage limit dashboard link is stale");

        var signal = QuotaClassifier.Classify(Profile(), process, Now);

        Assert.False(signal.Detected);
    }

    [Fact]
    public void A_generic_signature_in_stdout_alone_is_not_detected()
    {
        // Evidence is read from stderr only; a failing process whose stdout happens to mention a signature (e.g.
        // narrating unrelated work) must not be misread as quota exhaustion.
        var process = Process(1, stdout: "quota", stderr: "unrelated build failure");

        var signal = QuotaClassifier.Classify(Profile(), process, Now);

        Assert.False(signal.Detected);
    }

    [Fact]
    public void A_failed_process_matching_a_generic_signature_is_estimated_using_the_short_term_cooldown()
    {
        var process = Process(1, stderr: "Error: usage limit reached");

        var signal = QuotaClassifier.Classify(Profile(), process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaWindow.ShortTerm, signal.Window);
        Assert.Equal(QuotaResetKind.Estimated, signal.ResetKind);
        Assert.Equal(Now.AddHours(5), signal.ResetAt);
        Assert.Equal("usage limit", signal.Detail);
    }

    [Fact]
    public void A_weekly_signature_is_classified_separately_from_a_generic_signature_and_uses_its_own_cooldown()
    {
        var profile = Profile(weeklySignatures: ["weekly limit"], weeklyCooldownHours: 168);
        var process = Process(1, stderr: "You have reached your weekly limit. Also: quota exceeded.");

        var signal = QuotaClassifier.Classify(profile, process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaWindow.Weekly, signal.Window);
        Assert.Equal(QuotaResetKind.Estimated, signal.ResetKind);
        Assert.Equal(Now.AddHours(168), signal.ResetAt);
        Assert.Equal("weekly limit", signal.Detail);
    }

    [Fact]
    public void No_configured_signature_matching_leaves_no_quota_signal()
    {
        var process = Process(1, stderr: "network timeout");

        var signal = QuotaClassifier.Classify(Profile(), process, Now);

        Assert.Equal(QuotaSignal.None, signal);
    }

    [Fact]
    public void An_explicit_relative_reset_is_reported_and_parsed()
    {
        var profile = Profile(resetPattern: @"resets in (?<value>[^.\n]+)");
        var process = Process(1, stderr: "Error: quota exceeded. resets in 3h.");

        var signal = QuotaClassifier.Classify(profile, process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaResetKind.Reported, signal.ResetKind);
        Assert.Equal(Now.AddHours(3), signal.ResetAt);
    }

    [Fact]
    public void An_explicit_absolute_reset_is_reported_and_parsed()
    {
        var profile = Profile(resetPattern: @"resets at (?<value>\S+)");
        var process = Process(1, stderr: "Error: quota exceeded. resets at 2026-03-01T00:00:00Z");

        var signal = QuotaClassifier.Classify(profile, process, Now);

        Assert.Equal(QuotaResetKind.Reported, signal.ResetKind);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), signal.ResetAt);
    }

    [Fact]
    public void A_matched_but_unparseable_reset_falls_back_to_unknown_with_the_bounded_cooldown_not_a_reported_value()
    {
        var profile = Profile(resetPattern: @"resets in (?<value>[^.\n]+)");
        var process = Process(1, stderr: "Error: quota exceeded. resets in a little while.");

        var signal = QuotaClassifier.Classify(profile, process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaResetKind.Unknown, signal.ResetKind);
        Assert.Equal(Now.AddHours(5), signal.ResetAt);
    }

    [Fact]
    public void A_configured_pattern_that_never_matches_falls_back_to_estimated()
    {
        var profile = Profile(resetPattern: @"resets in (?<value>[^.\n]+)");
        var process = Process(1, stderr: "Error: quota exceeded.");

        var signal = QuotaClassifier.Classify(profile, process, Now);

        Assert.Equal(QuotaResetKind.Estimated, signal.ResetKind);
        Assert.Equal(Now.AddHours(5), signal.ResetAt);
    }

    [Fact]
    public void An_invalid_configured_pattern_is_never_fatal_and_falls_back_to_estimated()
    {
        var profile = Profile(resetPattern: "(unterminated[");
        var process = Process(1, stderr: "Error: quota exceeded.");

        var signal = QuotaClassifier.Classify(profile, process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaResetKind.Estimated, signal.ResetKind);
    }
}

public sealed class QuotaResetParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData("5h", 5)]
    [InlineData("5 hours", 5)]
    [InlineData("3.5h", 3.5)]
    [InlineData("1 hour", 1)]
    public void Parses_relative_hour_expressions(string value, double hours) =>
        Assert.Equal(Now.AddHours(hours), QuotaResetParser.TryParse(value, Now));

    [Theory]
    [InlineData("10m", 10)]
    [InlineData("10 minutes", 10)]
    public void Parses_relative_minute_expressions(string value, double minutes) =>
        Assert.Equal(Now.AddMinutes(minutes), QuotaResetParser.TryParse(value, Now));

    [Theory]
    [InlineData("2d", 2)]
    [InlineData("2 days", 2)]
    public void Parses_relative_day_expressions(string value, double days) =>
        Assert.Equal(Now.AddDays(days), QuotaResetParser.TryParse(value, Now));

    [Fact]
    public void Parses_an_absolute_iso_timestamp() =>
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), QuotaResetParser.TryParse("2026-03-01T00:00:00Z", Now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a little while")]
    [InlineData("soon")]
    [InlineData("5")]
    public void Returns_null_for_unrecognized_or_missing_values(string value) =>
        Assert.Null(QuotaResetParser.TryParse(value, Now));
}
