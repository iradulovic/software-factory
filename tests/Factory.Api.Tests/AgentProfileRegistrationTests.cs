using Factory.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

        Assert.Equal(["Codex-Luna", "Codex-Sol", "Claude"], runners.Select(r => r.Name));
        Assert.Equal(["Codex-Luna", "Codex-Sol", "Claude"], checkers.Select(c => c.Agent));
        Assert.Equal("Codex", runners[0].Provider);
        Assert.Equal("Codex", runners[1].Provider);
        Assert.Equal(("gpt-5.6-luna", "max"), (runners[0].Model, runners[0].ReasoningEffort));
        Assert.Equal(("gpt-5.6-sol", "medium"), (runners[1].Model, runners[1].ReasoningEffort));
    }

    public sealed class FactoryApplication : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
}
