using Factory.Core;

namespace Factory.Orchestrator.Tests;

public sealed class AgentSelectorTests
{
    [Fact]
    public async Task Deep_request_skips_a_provider_that_only_supports_quick()
    {
        var store = new FakeTaskStore();
        var quickOnly = new StubAgent("Codex", supportedClasses: ["quick"]);
        var deep = new StubAgent("Claude", supportedClasses: ["deep"]);
        var selector = new AgentSelector([quickOnly, deep], store);

        Assert.Same(deep, await selector.SelectAsync("Codex", CancellationToken.None, "deep"));
        Assert.Null(await new AgentSelector([quickOnly], store).SelectAsync("Codex", CancellationToken.None, "deep"));
    }

    [Fact]
    public async Task Prefers_the_tasks_preferred_agent_when_it_is_available()
    {
        var store = new FakeTaskStore();
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        var selected = await selector.SelectAsync("Claude", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task Falls_back_to_the_next_available_agent_when_the_preferred_one_is_at_quota()
    {
        var store = new FakeTaskStore();
        store.AgentsAtQuota.Add("Codex");
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        var selected = await selector.SelectAsync("Codex", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task Falls_back_to_the_next_available_agent_when_the_preferred_one_is_unauthenticated()
    {
        var store = new FakeTaskStore();
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store,
        [
            new StubAvailabilityChecker("Codex", "Codex", new AgentAvailability("Codex", false, "codex 1.2.3", "Authentication check failed")),
            new StubAvailabilityChecker("Claude", "Claude", new AgentAvailability("Claude", true, "claude 2.1.283", null))
        ]);

        var selected = await selector.SelectAsync("Codex", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task Returns_null_when_every_configured_agent_fails_authentication()
    {
        var store = new FakeTaskStore();
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Claude")], store,
        [
            new StubAvailabilityChecker("Codex", "Codex", new AgentAvailability("Codex", false, null, "Authentication check timed out")),
            new StubAvailabilityChecker("Claude", "Claude", new AgentAvailability("Claude", false, null, "Executable not found"))
        ]);

        Assert.Null(await selector.SelectAsync("Codex", CancellationToken.None));
    }

    [Fact]
    public async Task Pi_is_available_as_a_fallback_when_Codex_is_paused_and_Claude_is_at_quota()
    {
        var store = new FakeTaskStore();
        store.PausedAgents.Add("Codex");
        store.AgentsAtQuota.Add("Claude");
        var pi = new StubAgent("Pi", provider: "MoonshotAI", allowAutomaticFallback: true);
        var selector = new AgentSelector([new StubAgent("Codex-Luna", provider: "Codex"), new StubAgent("Claude"), pi], store);

        var selected = await selector.SelectAsync("Codex-Luna", CancellationToken.None);

        Assert.Same(pi, selected);
    }

    [Fact]
    public async Task Pi_is_not_used_as_an_automatic_fallback_without_explicit_cost_opt_in()
    {
        var store = new FakeTaskStore();
        store.PausedAgents.Add("Codex");
        store.AgentsAtQuota.Add("Claude");
        var pi = new StubAgent("Pi", provider: "MoonshotAI", allowAutomaticFallback: false);
        var selector = new AgentSelector([new StubAgent("Codex-Luna", provider: "Codex"), new StubAgent("Claude"), pi], store);

        Assert.Null(await selector.SelectAsync("Codex-Luna", CancellationToken.None));
        Assert.Same(pi, await selector.SelectAsync("Pi", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_when_every_configured_agent_is_at_quota()
    {
        var store = new FakeTaskStore();
        store.AgentsAtQuota.Add("Codex");
        store.AgentsAtQuota.Add("Claude");
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Claude")], store);

        Assert.Null(await selector.SelectAsync("Codex", CancellationToken.None));
    }

    [Theory]
    [InlineData("quick")]
    [InlineData("deep")]
    public async Task Grok_can_be_preferred_or_used_as_fallback_and_obeys_its_provider_pause(string taskClass)
    {
        var store = new FakeTaskStore();
        var grok = new StubAgent("Grok");
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Claude"), grok], store);

        Assert.Same(grok, await selector.SelectAsync("Grok", CancellationToken.None, taskClass));
        store.PausedAgents.Add("Codex");
        store.AgentsAtQuota.Add("Claude");
        Assert.Same(grok, await selector.SelectAsync("Codex", CancellationToken.None, taskClass));
        store.PausedAgents.Add("Grok");
        Assert.Null(await selector.SelectAsync("Grok", CancellationToken.None, taskClass));
    }

    [Fact]
    public async Task No_preference_uses_configured_order()
    {
        var store = new FakeTaskStore();
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        Assert.Same(codex, await selector.SelectAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Falls_back_to_the_next_available_agent_when_the_preferred_one_is_paused()
    {
        var store = new FakeTaskStore();
        store.PausedAgents.Add("Codex");
        var codex = new StubAgent("Codex");
        var claude = new StubAgent("Claude");
        var selector = new AgentSelector([codex, claude], store);

        var selected = await selector.SelectAsync("Codex", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task Returns_null_when_every_configured_agent_is_paused_or_at_quota()
    {
        var store = new FakeTaskStore();
        store.PausedAgents.Add("Codex");
        store.AgentsAtQuota.Add("Claude");
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Claude")], store);

        Assert.Null(await selector.SelectAsync("Codex", CancellationToken.None));
    }

    [Fact]
    public async Task A_preset_shares_its_base_profiles_quota_instead_of_getting_its_own_budget()
    {
        // SF-704: "Codex-High" is a distinct, independently selectable preset of the "Codex" provider. Quota
        // detected under the plain "Codex" profile must also block its preset, since both draw on the same
        // underlying subscription.
        var store = new FakeTaskStore();
        store.AgentsAtQuota.Add("Codex");
        var codex = new StubAgent("Codex", provider: "Codex");
        var codexHigh = new StubAgent("Codex-High", provider: "Codex");
        var claude = new StubAgent("Claude", provider: "Claude");
        var selector = new AgentSelector([codex, codexHigh, claude], store);

        var selected = await selector.SelectAsync("Codex-High", CancellationToken.None);

        Assert.Same(claude, selected);
    }

    [Fact]
    public async Task A_preset_is_available_whenever_its_base_profiles_quota_is_clear()
    {
        var store = new FakeTaskStore();
        var codexHigh = new StubAgent("Codex-High", provider: "Codex");
        var selector = new AgentSelector([new StubAgent("Codex", provider: "Codex"), codexHigh], store);

        var selected = await selector.SelectAsync("Codex-High", CancellationToken.None);

        Assert.Same(codexHigh, selected);
    }

    [Fact]
    public void KnownAgentNames_lists_every_configured_agent_and_preset_name()
    {
        var selector = new AgentSelector([new StubAgent("Codex"), new StubAgent("Codex-High", provider: "Codex"), new StubAgent("Claude")], new FakeTaskStore());

        Assert.Equal(["Codex", "Codex-High", "Claude"], selector.KnownAgentNames);
    }

    private sealed class StubAgent(string name, string? provider = null, bool allowAutomaticFallback = true, IReadOnlyList<string>? supportedClasses = null) : IAgentRunner
    {
        public string Name { get; } = name;
        public string Provider { get; } = provider ?? name;
        public bool AllowAutomaticFallback { get; } = allowAutomaticFallback;
        public bool SupportsTaskClass(string taskClass) => supportedClasses is null || supportedClasses.Contains(taskClass);
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubAvailabilityChecker(string agent, string provider, AgentAvailability result) : IAgentAvailabilityChecker
    {
        public string Agent { get; } = agent;
        public string Provider { get; } = provider;
        public Task<AgentAvailability> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
