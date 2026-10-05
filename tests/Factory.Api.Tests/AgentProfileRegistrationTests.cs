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
    public void API_and_orchestrator_use_the_same_Grok_profile_and_CLI_defaults()
    {
        var contentRoot = app.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        var orchestratorConfiguration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(contentRoot, "..", "Factory.Orchestrator", "appsettings.json")))
            .Build();
        var orchestratorProfile = orchestratorConfiguration.GetSection("Agents").Get<AgentProfilesOptions>()!.Profiles.Single(p => p.Name == "Grok");
        var apiProfile = app.Services.GetRequiredService<IConfiguration>().GetSection("Agents")
            .Get<AgentProfilesOptions>()!.Profiles.Single(p => p.Name == "Grok");

        Assert.Equal(apiProfile.Executable, orchestratorProfile.Executable);
        Assert.Equal(apiProfile.Arguments, orchestratorProfile.Arguments);
        Assert.Equal(apiProfile.AuthenticationArguments, orchestratorProfile.AuthenticationArguments);
        Assert.Equal(apiProfile.AuthenticationFailureSignatures, orchestratorProfile.AuthenticationFailureSignatures);
        Assert.Equal(apiProfile.PromptDelivery, orchestratorProfile.PromptDelivery);
        Assert.Equal(apiProfile.Provider, orchestratorProfile.Provider);
        Assert.Null(orchestratorProfile.Model);
        Assert.Null(orchestratorProfile.ReasoningEffort);
        Assert.Null(orchestratorProfile.Classes);
    }

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

        Assert.Equal(new[] { "Codex", "Claude", "Pi", "Grok" }, configuredProfiles.Select(p => p.Name).ToArray());
        Assert.Equal(["Codex", "Claude", "Pi", "Grok"], runners.Select(r => r.Name));
        Assert.Equal(["Codex", "Claude", "Pi", "Grok"], checkers.Select(c => c.Agent));
        Assert.Equal("Codex", runners[0].Provider);
        Assert.True(runners[0].AllowAutomaticFallback);
        Assert.True(runners[0].SupportsTaskClass("quick"));
        Assert.True(runners[0].SupportsTaskClass("deep"));
        Assert.False(runners[0].SupportsTaskClass("unknown"));
        Assert.Contains("read-only", configuredProfiles[0].ConversationArguments ?? []);
        Assert.NotNull(configuredProfiles[1].ConversationArguments);
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

        var grokProfile = configuredProfiles.Single(p => p.Name == "Grok");
        Assert.Equal(["--no-auto-update", "--permission-mode", "auto", "--sandbox", "workspace", "--output-format", "plain", "-p"], grokProfile.Arguments);
        Assert.Equal(["models"], grokProfile.AuthenticationArguments);
        Assert.Equal(["You are not authenticated."], grokProfile.AuthenticationFailureSignatures);
        Assert.Equal("argument", grokProfile.PromptDelivery);
        Assert.Null(grokProfile.Model);
        Assert.Null(grokProfile.ReasoningEffort);
        Assert.Null(grokProfile.Classes);
        Assert.Empty(grokProfile.QuotaSignatures ?? []);
        Assert.False(grokProfile.SupportsSessionResume);
        Assert.Equal("Grok", runners[3].Provider);
        Assert.True(runners[3].SupportsTaskClass("quick"));
        Assert.True(runners[3].SupportsTaskClass("deep"));
        Assert.True(runners[3].AllowAutomaticFallback);
    }

    public sealed class FactoryApplication : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
}
