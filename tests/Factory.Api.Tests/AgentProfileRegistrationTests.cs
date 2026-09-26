using Factory.Core;
using Factory.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Factory.Api.Tests;

public sealed class AgentProfileRegistrationTests : IClassFixture<AgentProfileRegistrationTests.FactoryApplication>
{
    private readonly FactoryApplication app;
    public AgentProfileRegistrationTests(FactoryApplication application) => app = application;

    [Fact]
    public void Each_configured_agent_profile_registers_exactly_one_runner_and_availability_checker()
    {
        // ConfigurationBinder.Get<T>() binds a configured List<T> section by appending to the target list.
        // The explicitly configured profiles must replace the built-in defaults at registration, not duplicate them.
        using var scope = app.Services.CreateScope();
        var runners = scope.ServiceProvider.GetServices<IAgentRunner>().ToList();
        var checkers = scope.ServiceProvider.GetServices<IAgentAvailabilityChecker>().ToList();
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var configuredProfiles = configuration.GetSection("Agents").Get<AgentProfilesOptions>()!.Profiles;

        Assert.Equal(new[] { "Codex", "Claude", "Pi" }, configuredProfiles.Select(p => p.Name).ToArray());
        Assert.Equal(["Codex", "Claude", "Pi"], runners.Select(r => r.Name));
        Assert.Equal(["Codex", "Claude", "Pi"], checkers.Select(c => c.Agent));
        Assert.Equal("Codex", runners[0].Provider);
        Assert.True(runners[0].AllowAutomaticFallback);
        Assert.True(runners[0].SupportsTaskClass("quick"));
        Assert.True(runners[0].SupportsTaskClass("deep"));
        Assert.False(runners[0].SupportsTaskClass("unknown"));
        Assert.Equal(("MoonshotAI", "moonshotai/kimi-k2.6", null), (runners[2].Provider, runners[2].Model, runners[2].ReasoningEffort));

        Assert.Equal(["login", "status"], configuredProfiles.Single(p => p.Name == "Codex").AuthenticationArguments);
        Assert.Equal(["auth", "status"], configuredProfiles.Single(p => p.Name == "Claude").AuthenticationArguments);

        var piProfile = configuredProfiles.Single(p => p.Name == "Pi");
        Assert.Equal(["--print", "--model", "moonshotai/kimi-k2.6"], piProfile.Arguments);
        Assert.Equal(["auth", "check", "--model", "moonshotai/kimi-k2.6", "--no-refresh"], piProfile.AuthenticationArguments);
        Assert.Equal("stdin", piProfile.PromptDelivery);
        Assert.Empty(piProfile.QuotaSignatures ?? []);
        Assert.Empty(piProfile.WeeklyQuotaSignatures ?? []);
        Assert.False(piProfile.AllowAutomaticFallback);
    }

    public sealed class FactoryApplication : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
}
