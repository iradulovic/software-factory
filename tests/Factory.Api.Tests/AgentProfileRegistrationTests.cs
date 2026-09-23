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
        // Regression test: AgentProfilesOptions.Profiles previously defaulted to a non-empty list containing the
        // built-in Codex profile. ConfigurationBinder.Get<T>() binds a configured List<T> section by appending
        // to whatever the target list already contains rather than replacing it, so appsettings.json's own
        // "Agents:Profiles" (matching the built-in default entries) ended up bound alongside that default instead
        // of in place of it — registering every configured agent, and so every IAgentRunner and
        // IAgentAvailabilityChecker, twice.
        using var scope = app.Services.CreateScope();
        var runners = scope.ServiceProvider.GetServices<IAgentRunner>().ToList();
        var checkers = scope.ServiceProvider.GetServices<IAgentAvailabilityChecker>().ToList();

        Assert.Equal(["Codex", "Claude"], runners.Select(r => r.Name));
        Assert.Equal(["Codex", "Claude"], checkers.Select(c => c.Agent));
    }

    public sealed class FactoryApplication : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }
}
