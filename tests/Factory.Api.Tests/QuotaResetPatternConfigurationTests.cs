using Factory.Core;
using Factory.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Api.Tests;

/// <summary>
/// Regression coverage for the default Codex and Claude profiles' configured <c>QuotaResetPattern</c>
/// (appsettings.json): reads the actual bound configuration, the same way <c>ServiceCollectionExtensions</c>
/// does, so a future edit to either appsettings.json that breaks extraction fails here rather than silently
/// falling back to the flat <c>QuotaCooldownHours</c> estimate again.
/// </summary>
public sealed class QuotaResetPatternConfigurationTests : IClassFixture<QuotaResetPatternConfigurationTests.FactoryApplication>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private readonly FactoryApplication app;
    public QuotaResetPatternConfigurationTests(FactoryApplication application) => app = application;

    private AgentProfile Profile(string name) =>
        app.Services.GetRequiredService<IConfiguration>().GetSection("Agents").Get<AgentProfilesOptions>()!.Profiles
            .Single(p => p.Name == name);

    [Fact]
    public void Codexs_configured_pattern_captures_a_reported_wall_clock_reset_time()
    {
        var process = new ProcessResult("codex", [], ".", Now, Now, 1, "", "Error: usage limit reached. You can try again at 5:12 PM.", false, false);

        var signal = QuotaClassifier.Classify(Profile("Codex-Luna"), process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaResetKind.Reported, signal.ResetKind);
        Assert.NotNull(signal.ResetAt);
    }

    [Fact]
    public void Claudes_configured_pattern_captures_a_reported_relative_reset_duration()
    {
        var process = new ProcessResult("claude", [], ".", Now, Now, 1, "", "Error: rate limited. resets in 3h.", false, false);

        var signal = QuotaClassifier.Classify(Profile("Claude"), process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaResetKind.Reported, signal.ResetKind);
        Assert.Equal(Now.AddHours(3), signal.ResetAt);
    }

    [Fact]
    public void An_unrecognized_message_still_falls_back_to_the_bounded_estimate_rather_than_failing()
    {
        var process = new ProcessResult("codex", [], ".", Now, Now, 1, "", "Error: quota exceeded.", false, false);

        var signal = QuotaClassifier.Classify(Profile("Codex-Luna"), process, Now);

        Assert.True(signal.Detected);
        Assert.Equal(QuotaResetKind.Estimated, signal.ResetKind);
        Assert.Equal(Now.AddHours(5), signal.ResetAt);
    }

    public sealed class FactoryApplication : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
}
